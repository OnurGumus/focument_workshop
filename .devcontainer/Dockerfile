# Reproducible toolchain for the workshop. .NET 11 is still preview and C# 15
# `union` needs LangVersion preview, so we install the *exact* SDK pinned in
# global.json with the official script rather than depend on a moving image tag —
# 30 attendee laptops should all build the same thing. Keep DOTNET_VERSION in sync
# with global.json (rollForward there means a clean exact install satisfies it).
FROM mcr.microsoft.com/devcontainers/base:ubuntu-24.04

ARG DOTNET_VERSION=11.0.100-preview.4.26230.115
RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
 && bash /tmp/dotnet-install.sh --version "${DOTNET_VERSION}" --install-dir /usr/share/dotnet \
 && ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet \
 && rm /tmp/dotnet-install.sh

ENV DOTNET_ROOT=/usr/share/dotnet \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1
