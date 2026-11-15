FROM ghcr.io/gitleaks/gitleaks@sha256:c00b6bd0aeb3071cbcb79009cb16a60dd9e0a7c60e2be9ab65d25e6bc8abbb7f AS scanner
FROM mcr.microsoft.com/dotnet/sdk@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e
COPY --from=scanner /usr/bin/gitleaks /usr/local/bin/gitleaks
COPY gitleaks.toml /etc/codexflow/gitleaks.toml
COPY empty-ignore /etc/codexflow/empty-ignore
