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
        iproute2 \
        jq \
        less \
        procps \
        python3 \
        unzip \
        zip \
    && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /opt/opencode /home/opencode \
    && chmod 755 /opt/opencode \
    && chmod 777 /home/opencode

RUN HOME=/opt/opencode bash -lc "curl -fsSL https://opencode.ai/install | bash -s -- --no-modify-path"

WORKDIR /workspace

ENV PATH="/opt/opencode/.opencode/bin:/opt/opencode/.local/share/opencode/bin:/opt/opencode/.local/bin:${PATH}"
