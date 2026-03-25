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
        gnuplot \
        iproute2 \
        jq \
        less \
        miller \
        procps \
        python3 \
        python-is-python3 \
        python3-pip \
        python3-venv \
        sqlite3 \
        unzip \
        zip \
    && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /etc/apt/keyrings \
    && curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key | gpg --dearmor -o /etc/apt/keyrings/nodesource.gpg \
    && chmod go+r /etc/apt/keyrings/nodesource.gpg \
    && echo "deb [signed-by=/etc/apt/keyrings/nodesource.gpg] https://deb.nodesource.com/node_24.x nodistro main" > /etc/apt/sources.list.d/nodesource.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends nodejs \
    && rm -rf /var/lib/apt/lists/*

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

RUN mkdir -p /ocw \
    && chmod 777 /ocw

WORKDIR /workspace

ENV PATH="/opt/ds/bin:${PATH}"
