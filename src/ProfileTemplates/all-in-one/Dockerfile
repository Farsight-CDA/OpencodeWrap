FROM ubuntu:26.04

ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        bash \
        binutils \
        ca-certificates \
        clang \
        coreutils \
        curl \
        dotnet-sdk-10.0 \
        file \
        gh \
        git \
        gnuplot \
        gpg \
        iproute2 \
        jq \
        less \
        libssl-dev \
        miller \
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

RUN arch="$(dpkg --print-architecture)" \
    && case "$arch" in \
        amd64) go_arch='amd64' ;; \
        arm64) go_arch='arm64' ;; \
        *) echo "Unsupported Go architecture: $arch" >&2; exit 1 ;; \
    esac \
    && go_version="$(curl -fsSL https://go.dev/dl/?mode=json | jq -r 'map(select(.stable)) | first | .version')" \
    && curl -fsSL "https://go.dev/dl/${go_version}.linux-${go_arch}.tar.gz" -o /tmp/go.tgz \
    && rm -rf /usr/local/go \
    && tar -C /usr/local -xzf /tmp/go.tgz \
    && rm /tmp/go.tgz

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

RUN curl https://sh.rustup.rs -sSf | bash -s -- -y --profile minimal \
    && /root/.cargo/bin/rustup component add rustfmt

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

RUN mkdir -p /ocw \
    && chmod 777 /ocw

WORKDIR /workspace

ENV PATH="/opt/ds/bin:/root/.dotnet/tools:/root/.bun/bin:/root/.cargo/bin:/root/.foundry/bin:/usr/local/go/bin:${PATH}"
ENV BaseIntermediateOutputPath="obj/opencode-wrap/"
ENV DefaultItemExcludesInProjectFolder="$(DefaultItemExcludesInProjectFolder);obj/**"
