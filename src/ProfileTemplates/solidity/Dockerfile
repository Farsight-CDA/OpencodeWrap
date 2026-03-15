FROM ubuntu:24.04

ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        bash \
        ca-certificates \
        coreutils \
        curl \
        file \
        gh \
        git \
        gpg \
        iproute2 \
        jq \
        less \
        procps \
        python3 \
        unzip \
        xz-utils \
        zip \
    && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /etc/apt/keyrings \
    && curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key | gpg --dearmor -o /etc/apt/keyrings/nodesource.gpg \
    && chmod go+r /etc/apt/keyrings/nodesource.gpg \
    && echo "deb [signed-by=/etc/apt/keyrings/nodesource.gpg] https://deb.nodesource.com/node_22.x nodistro main" > /etc/apt/sources.list.d/nodesource.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends nodejs \
    && rm -rf /var/lib/apt/lists/*

ARG SOLC_VERSION=0.8.30

RUN curl -L https://foundry.paradigm.xyz | bash \
    && /root/.foundry/bin/foundryup

RUN npm install -g --no-fund --no-audit \
        hardhat \
        prettier \
        prettier-plugin-solidity \
        solc@${SOLC_VERSION} \
        solhint

RUN printf '%s\n' \
        '#!/usr/bin/env bash' \
        'exec solcjs "$@"' \
        > /usr/local/bin/solc \
    && chmod +x /usr/local/bin/solc

RUN mkdir -p /opt/opencode /ocw \
    && chmod 755 /opt/opencode \
    && chmod 777 /ocw

RUN HOME=/opt/opencode bash -lc "curl -fsSL https://opencode.ai/install | bash -s -- --no-modify-path"

WORKDIR /workspace

ENV PATH="/root/.foundry/bin:/opt/opencode/.opencode/bin:/opt/opencode/.local/share/opencode/bin:/opt/opencode/.local/bin:${PATH}"
