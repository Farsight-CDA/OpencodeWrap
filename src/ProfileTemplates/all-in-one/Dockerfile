FROM ubuntu:26.04

ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ansible-core \
        bash \
        binutils \
        ca-certificates \
        clang \
        coreutils \
        curl \
        file \
        gh \
        git \
        git-lfs \
        gnuplot \
        gpg \
        iproute2 \
        jq \
        less \
        libssl-dev \
        miller \
        openssh-client \
        postgresql-client \
        procps \
        python3 \
        python-is-python3 \
        python3-pip \
        python3-venv \
        rsync \
        sqlite3 \
        unzip \
        xdg-utils \
        xz-utils \
        zlib1g-dev \
        zip \
    && rm -rf /var/lib/apt/lists/*

RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && chmod +x /tmp/dotnet-install.sh \
    && /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet --no-path \
    && /tmp/dotnet-install.sh --channel 11.0 --install-dir /usr/share/dotnet --no-path \
    && ln -s /usr/share/dotnet/dotnet /usr/local/bin/dotnet \
    && rm /tmp/dotnet-install.sh

RUN mkdir -p /etc/apt/keyrings \
    && curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key | gpg --dearmor -o /etc/apt/keyrings/nodesource.gpg \
    && chmod go+r /etc/apt/keyrings/nodesource.gpg \
    && echo "deb [signed-by=/etc/apt/keyrings/nodesource.gpg] https://deb.nodesource.com/node_26.x nodistro main" > /etc/apt/sources.list.d/nodesource.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends \
        nodejs \
    && rm -rf /var/lib/apt/lists/*

RUN npm install -g --no-fund --no-audit npm@latest corepack@latest \
    && corepack enable \
    && corepack install --global yarn@stable pnpm@latest

RUN dotnet tool install --global dotnet-t4

COPY --from=golang:1.27-bookworm /usr/local/go/ /usr/local/go/

ARG PROTOC_VERSION=36.2

RUN protoc_arch="$(uname -m | sed 's/aarch64/aarch_64/')" \
    && curl -fsSL "https://github.com/protocolbuffers/protobuf/releases/download/v${PROTOC_VERSION}/protoc-${PROTOC_VERSION}-linux-${protoc_arch}.zip" -o /tmp/protoc.zip \
    && unzip -q /tmp/protoc.zip 'bin/*' 'include/*' -d /usr/local \
    && rm /tmp/protoc.zip

ARG SOLC_VERSION=0.8.38

RUN npm install -g --no-fund --no-audit \
    @bitwarden/cli \
    @playwright/test \
    eslint \
    hardhat \
    http-server \
    jest \
    prettier \
    prettier-plugin-solidity \
    serve \
    solc@${SOLC_VERSION} \
    solhint \
    typescript \
    vite \
    vitest

RUN curl -fsSL https://bun.sh/install | bash

RUN curl https://sh.rustup.rs -sSf | bash -s -- -y --profile minimal --component rustfmt

RUN curl -L https://foundry.paradigm.xyz | bash \
    && /root/.foundry/bin/foundryup

RUN python3 -m venv /opt/ds \
    && /opt/ds/bin/pip install --no-cache-dir --upgrade pip \
    && /opt/ds/bin/pip install --no-cache-dir \
        altair \
        csvkit \
        duckdb \
        jupyterlab \
        matplotlib \
        numpy \
        openpyxl \
        pandas \
        plotly \
        polars \
        pyarrow \
        seaborn \
        sqlite-utils \
        visidata

RUN printf '%s\n' \
        '#!/usr/bin/env bash' \
        'exec solcjs "$@"' \
        > /usr/local/bin/solc \
    && chmod +x /usr/local/bin/solc

RUN npm install -g --no-fund --no-audit \
    --allow-scripts=@anthropic-ai/claude-code @anthropic-ai/claude-code@stable

RUN mkdir -p /ocw \
    && chmod 777 /ocw

WORKDIR /workspace

ENV PATH="/opt/ds/bin:/root/.dotnet/tools:/root/.bun/bin:/root/.cargo/bin:/root/.foundry/bin:/usr/local/go/bin:${PATH}"
ENV CLAUDE_CONFIG_DIR=/ocw/state/claude-code
ENV DISABLE_AUTOUPDATER=1
ENV BaseIntermediateOutputPath="obj/opencode-wrap/"
ENV DefaultItemExcludesInProjectFolder="$(DefaultItemExcludesInProjectFolder);obj/**"
