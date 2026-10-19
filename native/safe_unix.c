#define _GNU_SOURCE
#include <sys/stat.h>
#include <sys/file.h>
#include <fcntl.h>
#include <unistd.h>
#include <errno.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <stdint.h>

/* Anchor every component to a descriptor. Never traverse a symlink, including
   one substituted after the managed scan. No recursive deletion is performed. */
static int open_directory(const char *path) {
    if (!path || path[0] != '/') return -1;
    int fd = open("/", O_RDONLY | O_DIRECTORY | O_CLOEXEC);
    char *copy = strdup(path), *save = NULL;
    if (fd < 0 || !copy) { if (fd >= 0) close(fd); free(copy); return -1; }
    for (char *part = strtok_r(copy, "/", &save); part; part = strtok_r(NULL, "/", &save)) {
        if (!strcmp(part, ".") || !strcmp(part, "..")) { close(fd); fd = -1; break; }
        int next = openat(fd, part, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
        close(fd); fd = next;
        if (fd < 0) break;
    }
    free(copy);
    return fd;
}

static int same_file(const struct stat *a, const struct stat *b) {
    return S_ISREG(b->st_mode) && a->st_dev == b->st_dev && a->st_ino == b->st_ino &&
        a->st_size == b->st_size && a->st_mtime == b->st_mtime && b->st_nlink == 1;
}

/* Return 1=deleted, 0=protected/unavailable, -2=quarantined for manual recovery.
   Rename into a private directory before unlinking: a replaced directory entry
   can never cause a different file to be silently deleted. */
int el_delete_old(const char *root, const char *relative, int64_t cutoff, int64_t *bytes) {
    *bytes = 0;
    if (!relative || !*relative || relative[0] == '/') return 0;
    int rootfd = open_directory(root);
    if (rootfd < 0) return 0;
    int dirfd = dup(rootfd), filefd = -1, qfd = -1, result = 0;
    char *copy = strdup(relative), *save = NULL;
    char quarantine[96] = "";
    if (dirfd < 0 || !copy) goto done;
    char *part = strtok_r(copy, "/", &save);
    if (!part) goto done;
    for (;;) {
        if (!strcmp(part, ".") || !strcmp(part, "..")) goto done;
        char *next = strtok_r(NULL, "/", &save);
        if (!next) break;
        int child = openat(dirfd, part, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
        if (child < 0) goto done;
        close(dirfd); dirfd = child; part = next;
    }
    filefd = openat(dirfd, part, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC);
    struct stat before, entry, moved;
    if (filefd < 0 || fstat(filefd, &before) || !S_ISREG(before.st_mode) ||
        before.st_uid != geteuid() || before.st_nlink != 1 || !(before.st_mode & S_IWUSR) ||
        before.st_mtime >= cutoff || before.st_ctime >= cutoff ||
        flock(filefd, LOCK_EX | LOCK_NB) ||
        fstatat(dirfd, part, &entry, AT_SYMLINK_NOFOLLOW) || !same_file(&before, &entry)) goto done;
    /* mkdirat is exclusive. Collisions never reuse an existing directory. */
    for (int attempt = 0; attempt < 100; attempt++) {
        snprintf(quarantine, sizeof quarantine, ".espaco-leve-recovery-%ld-%d", (long)getpid(), attempt);
        if (!mkdirat(rootfd, quarantine, 0700)) break;
        quarantine[0] = '\0';
        if (errno != EEXIST) goto done;
    }
    if (!quarantine[0]) goto done;
    qfd = openat(rootfd, quarantine, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
    if (qfd < 0 || renameat(dirfd, part, qfd, "candidate")) goto done;
    if (fstatat(qfd, "candidate", &moved, AT_SYMLINK_NOFOLLOW) || !same_file(&before, &moved) ||
        fstat(filefd, &entry) || !same_file(&before, &entry)) {
        /* linkat refuses to overwrite a newly created original. Preserve the
           quarantined entry if it cannot be restored without overwriting. */
        if (!linkat(qfd, "candidate", dirfd, part, 0)) unlinkat(qfd, "candidate", 0);
        else result = -2;
        goto done;
    }
    if (!unlinkat(qfd, "candidate", 0)) { *bytes = before.st_size; result = 1; }
    else {
        if (!linkat(qfd, "candidate", dirfd, part, 0)) unlinkat(qfd, "candidate", 0);
        else result = -2;
    }
done:
    if (qfd >= 0) close(qfd);
    if (quarantine[0]) unlinkat(rootfd, quarantine, AT_REMOVEDIR); /* empty only */
    if (filefd >= 0) close(filefd);
    if (dirfd >= 0) close(dirfd);
    close(rootfd);
    free(copy);
    return result;
}
