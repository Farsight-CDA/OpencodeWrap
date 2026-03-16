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
        iproute2 \
        jq \
        less \
        procps \
        python3 \
        python-is-python3 \
        unzip \
        zip \
    && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /ocw \
    && chmod 777 /ocw

WORKDIR /workspace
