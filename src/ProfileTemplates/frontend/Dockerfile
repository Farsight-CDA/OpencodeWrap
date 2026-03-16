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
        python-is-python3 \
        unzip \
        xdg-utils \
        zip \
    && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /etc/apt/keyrings \
    && curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key | gpg --dearmor -o /etc/apt/keyrings/nodesource.gpg \
    && chmod go+r /etc/apt/keyrings/nodesource.gpg \
    && echo "deb [signed-by=/etc/apt/keyrings/nodesource.gpg] https://deb.nodesource.com/node_22.x nodistro main" > /etc/apt/sources.list.d/nodesource.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends nodejs \
    && rm -rf /var/lib/apt/lists/*

RUN corepack enable \
    && corepack prepare yarn@stable --activate \
    && corepack prepare pnpm@latest --activate

RUN npm install -g --no-fund --no-audit \
        @playwright/test \
        eslint \
        http-server \
        jest \
        prettier \
        serve \
        typescript \
        vite \
        vitest

RUN curl -fsSL https://bun.sh/install | bash

RUN mkdir -p /ocw \
    && chmod 777 /ocw

WORKDIR /workspace

ENV PATH="/root/.bun/bin:${PATH}"
