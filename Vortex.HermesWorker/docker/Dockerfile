FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    HERMES_HOME=/home/hermes/.hermes

RUN python -m pip install --no-cache-dir --upgrade pip \
    && python -m pip install --no-cache-dir hermes-agent==0.18.2 \
    && apt-get update \
    && apt-get install -y --no-install-recommends gosu \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --system hermes \
    && useradd --system --create-home --gid hermes --home-dir /home/hermes --shell /usr/sbin/nologin hermes \
    && mkdir -p /workspace "$HERMES_HOME" \
    && chown -R hermes:hermes /workspace /home/hermes

COPY docker/hermes/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh

WORKDIR /workspace
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
