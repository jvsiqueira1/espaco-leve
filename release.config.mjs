export default {
  branches: ["main"],
  repositoryUrl: "https://github.com/jvsiqueira1/espaco-leve.git",
  tagFormat: "v${version}",
  plugins: [
    "@semantic-release/commit-analyzer",
    "@semantic-release/release-notes-generator",
    "./scripts/release-assets.mjs",
    ["@semantic-release/github", {
      assets: [{ path: "artifacts/release/*" }],
      successComment: false,
      failComment: false,
      releasedLabels: false,
      addReleases: false
    }]
  ]
};
