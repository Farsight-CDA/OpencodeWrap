FROM ubuntu:24.04

ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        bash \
        binutils \
        ca-certificates \
        clang \
        coreutils \
        curl \
        file \
        gh \
        git \
        gnuplot \
        gpg \
        iproute2 \
        jq \
        less \
        miller \
        postgresql-client \
        procps \
        python3 \
        python-is-python3 \
        python3-pip \
        python3-venv \
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
    && echo "deb [signed-by=/etc/apt/keyrings/nodesource.gpg] https://deb.nodesource.com/node_24.x nodistro main" > /etc/apt/sources.list.d/nodesource.list \
    && curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /etc/apt/keyrings/microsoft.gpg \
    && chmod go+r /etc/apt/keyrings/microsoft.gpg \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/microsoft.gpg] https://packages.microsoft.com/ubuntu/24.04/prod noble main" > /etc/apt/sources.list.d/microsoft-prod.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends \
        dotnet-sdk-10.0 \
        nodejs \
    && rm -rf /var/lib/apt/lists/*

RUN corepack enable \
    && corepack prepare yarn@stable --activate \
    && corepack prepare pnpm@latest --activate

ARG SOLC_VERSION=0.8.30

RUN npm install -g --no-fund --no-audit \
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

RUN curl https://sh.rustup.rs -sSf | bash -s -- -y --profile minimal

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

ENV PATH="/opt/ds/bin:/root/.bun/bin:/root/.cargo/bin:/root/.foundry/bin:${PATH}"
ENV BaseIntermediateOutputPath="obj/opencode-wrap/"
ENV DefaultItemExcludesInProjectFolder="$(DefaultItemExcludesInProjectFolder);obj/**"
