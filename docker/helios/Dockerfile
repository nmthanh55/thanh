# HeliosDB-Nano built from crates.io (the project publishes no official image).
# The first build compiles RocksDB and takes 10-20 minutes.
FROM rust:1-bookworm AS build
RUN apt-get update \
 && apt-get install -y --no-install-recommends clang libclang-dev cmake \
 && rm -rf /var/lib/apt/lists/*
ARG HELIOS_VERSION=4.41.1
RUN cargo install heliosdb-nano --version ${HELIOS_VERSION} --locked

FROM debian:bookworm-slim
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates libssl3 \
 && rm -rf /var/lib/apt/lists/*
COPY --from=build /usr/local/cargo/bin/heliosdb-nano /usr/local/bin/heliosdb-nano
VOLUME /data
EXPOSE 5432
# Trust auth for local test use; docker-compose publishes the port on 127.0.0.1 only.
CMD ["heliosdb-nano", "start", "--data-dir", "/data", "--listen", "0.0.0.0", "--allow-insecure-trust", "--http-port", "0"]
