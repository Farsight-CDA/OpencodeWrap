FROM ubuntu:24.04

ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        bash \
        ca-certificates \
        coreutils \
        curl \
        file \
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

RUN curl -L https://foundry.paradigm.xyz | bash \
    && /root/.foundry/bin/foundryup

ARG SOLC_VERSION=v0.8.30

RUN set -eux; \
    arch="$(uname -m)"; \
    case "$arch" in \
      x86_64) solc_arch="linux-amd64" ;; \
      aarch64) solc_arch="linux-aarch64" ;; \
      *) echo "Unsupported architecture for solc: $arch"; exit 1 ;; \
    esac; \
    curl -fsSL -o /usr/local/bin/solc "https://github.com/ethereum/solidity/releases/download/${SOLC_VERSION}/solc-static-${solc_arch}"; \
    chmod +x /usr/local/bin/solc

RUN npm install -g --no-fund --no-audit \
        hardhat \
        prettier \
        prettier-plugin-solidity \
        solhint

RUN mkdir -p /opt/opencode /home/opencode \
    && chmod 755 /opt/opencode \
    && chmod 777 /home/opencode

RUN HOME=/opt/opencode bash -lc "curl -fsSL https://opencode.ai/install | bash -s -- --no-modify-path"

WORKDIR /workspace

ENV PATH="/root/.foundry/bin:/opt/opencode/.opencode/bin:/opt/opencode/.local/share/opencode/bin:/opt/opencode/.local/bin:${PATH}"
