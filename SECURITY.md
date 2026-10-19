# Segurança

Relatos de exclusão fora das regras devem incluir sistema, versão e categoria, sem anexar documentos pessoais ou o conteúdo dos caches. Pause a rotina enquanto o caso é investigado.

O projeto usa uma lista de locais conhecidos; não oferece execução de comandos arbitrários ou remoção recursiva de pastas pela interface. O auxiliar Unix faz parte da proteção e deve ser compilado/testado no mesmo sistema e arquitetura do pacote.

## Dependências de publicação

Em 19/10/2026, o npm audit aponta GHSA-vfj7-8cjw-p6xm em braces 3.0.3, dependência transitiva do semantic-release. A versão mais recente consultada ainda não possui correção. O problema envolve padrões glob profundamente aninhados.

Os padrões usados por este projeto são constantes no repositório e não recebem entradas de usuários. Essas dependências servem apenas para publicação no CI e não são incluídas no aplicativo desktop. Não foi aplicado o downgrade incompatível sugerido automaticamente pelo npm. Atualizar quando houver correção publicada e validar novamente os testes de versões. A auditoria não deve ser apresentada como livre de alertas enquanto esse aviso persistir.
