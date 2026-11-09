# Asterisk base image built from the official, signed release tarball.
#
# This is the andrius build recipe (MIT, see NOTICE): its generated three-stage Dockerfile for Debian trixie,
# parametrised by version instead of generated once per version. The build flags, the module set, the runtime
# packages, the `asterisk` user (uid 1000), the paths, `make samples`, the entrypoint, the healthcheck and the CMD are
# the recipe's. What differs:
#   - the Debian base is pinned by digest, so a rebuild starts from the same system;
#   - the version and the tarball's sha256 are build arguments; the sha256 comes from versions.txt, whose tarball the
#     publishing workflow checks against the Asterisk development team's signature before it builds;
#   - build.sh does not download the proprietary codec_opus binary;
#   - the final image carries OCI labels and /usr/share/doc/verbara-asterisk-base/{SOURCE.txt,NOTICE} naming the
#     exact source it was built from (Asterisk and its bundled pjproject are GPLv2).
#
# Configuration is never baked in: a consumer mounts its own /etc/asterisk. Sources and build flags are never patched;
# a different Asterisk is a different line of versions.txt, not an edit here.
#
#   docker build --build-arg ASTERISK_VERSION=<version> --build-arg TARBALL_SHA256=<sha256> \
#     --build-arg RECIPE_REVISION=<commit of docker/base> -t <image>:<tag> docker/base

# ==============================================================================
# STAGE 1: Build Environment
# ==============================================================================
FROM debian:trixie@sha256:9c6e381421d0f48d1b8133e573f33c185ee16cbf505eda9c4a8a4791b25ea8a6 AS asterisk-builder

# Build arguments
ARG ASTERISK_VERSION
ARG TARBALL_SHA256
ARG JOBS=8
ARG DEBIAN_FRONTEND=noninteractive
ARG TARGETARCH

# Refuse a build without a well-formed version and checksum before any download.
RUN echo "${ASTERISK_VERSION}" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+(-rc[0-9]+)?$' \
        || { echo "ASTERISK_VERSION '${ASTERISK_VERSION}' is not <major>.<minor>.<patch>[-rcN]" >&2; exit 1; } \
    && echo "${TARBALL_SHA256}" | grep -Eq '^[0-9a-f]{64}$' \
        || { echo "TARBALL_SHA256 '${TARBALL_SHA256}' is not a sha256" >&2; exit 1; }

# Environment variables
ENV ASTERISK_VERSION=${ASTERISK_VERSION}
ENV DEBIAN_FRONTEND=${DEBIAN_FRONTEND}
ENV TMPDIR="/tmp/asterisk_build"

# Create build directories first
RUN mkdir -p \
    /usr/src/asterisk \
    ${TMPDIR} \
    && chmod 777 ${TMPDIR}

# Update package lists and install ca-certificates first, then build dependencies
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates \
    && apt-get install -y --no-install-recommends \
        build-essential autoconf binutils-dev curl file libcurl4-openssl-dev \
        libedit-dev libgsm1-dev libicu-dev libjansson-dev libncurses-dev libogg-dev \
        libpopt-dev libpqxx-dev libresample1-dev libspandsp-dev libspeex-dev \
        libspeexdsp-dev libsqlite3-dev libssl-dev libtool libvorbis-dev libxml2-dev \
        libxslt1-dev make odbcinst patch pkg-config portaudio19-dev procps \
        python3-dev unixodbc unixodbc-dev uuid uuid-dev xmlstarlet libsrtp2-dev \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p ${TMPDIR} && chmod 777 ${TMPDIR}

WORKDIR /usr/src/asterisk

# Download the official tarball and verify its sha256 before extracting.
RUN curl -fsSL -o /tmp/asterisk-src.tar.gz https://downloads.asterisk.org/pub/telephony/asterisk/releases/asterisk-${ASTERISK_VERSION}.tar.gz \
    && echo "${TARBALL_SHA256}  /tmp/asterisk-src.tar.gz" | sha256sum -c - \
    && tar --strip-components=1 -xzf /tmp/asterisk-src.tar.gz \
    && rm /tmp/asterisk-src.tar.gz

# Configure Asterisk build
RUN ./configure

# Generate menuselect configuration
RUN make menuselect/menuselect menuselect-tree menuselect.makeopts

# Copy and execute build script
COPY build.sh /usr/src/asterisk/build.sh
RUN chmod +x build.sh && ./build.sh

# ==============================================================================
# STAGE 2: Runtime Environment
# ==============================================================================
FROM debian:trixie@sha256:9c6e381421d0f48d1b8133e573f33c185ee16cbf505eda9c4a8a4791b25ea8a6 AS asterisk-runtime

ARG DEBIAN_FRONTEND=noninteractive
ARG TARGETARCH
ENV DEBIAN_FRONTEND=${DEBIAN_FRONTEND}

# Install only runtime dependencies
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        binutils ca-certificates curl gettext-base libedit2 libgsm1 libjansson4 \
        libogg0 libopus0 libpopt0 libresample1 libspandsp2 libspeex1 libspeexdsp1 \
        libsqlite3-0 libvorbis0a libxml2 libxslt1.1 odbcinst portaudio19-dev procps \
        python3 unixodbc uuid libcurl4t64 libssl3t64 libsrtp2-1 libncurses6 \
        libpqxx-7.10 libicu76 \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

# Create asterisk user and required directories
RUN useradd --uid 1000 --system --user-group \
    --home-dir /home/asterisk --create-home \
    asterisk \
    && mkdir -p \
        /var/run/asterisk \
        /var/log/asterisk \
        /var/spool/asterisk/monitor \
        /var/spool/asterisk/outgoing \
        /var/spool/asterisk/tmp \
        /var/spool/asterisk/voicemail \
        /var/spool/asterisk/fax \
        /var/lib/asterisk/keys \
        /var/lib/asterisk/phoneprov \
        /var/lib/asterisk/sounds \
        /var/lib/asterisk/docs

# ==============================================================================
# STAGE 3: Final Production Image
# ==============================================================================
FROM asterisk-runtime AS asterisk-production

ARG ASTERISK_VERSION
ARG TARBALL_SHA256
ARG RECIPE_REVISION
ARG SOURCE_REPOSITORY=https://github.com/verbara/Verbara.Sdk

LABEL org.opencontainers.image.title="Asterisk ${ASTERISK_VERSION} base"
LABEL org.opencontainers.image.description="Asterisk ${ASTERISK_VERSION} built from the official GPG-verified release tarball on Debian trixie"
LABEL org.opencontainers.image.version="${ASTERISK_VERSION}"
LABEL org.opencontainers.image.revision="${RECIPE_REVISION}"
LABEL org.opencontainers.image.source="${SOURCE_REPOSITORY}"
LABEL org.opencontainers.image.licenses="GPL-2.0-only"
LABEL org.opencontainers.image.vendor="Verbara"
LABEL org.verbara.asterisk.tarball-sha256="${TARBALL_SHA256}"

# Copy Asterisk binaries and configurations from builder
COPY --from=asterisk-builder /usr/sbin/asterisk /usr/sbin/asterisk
COPY --from=asterisk-builder /usr/lib/asterisk /usr/lib/asterisk
COPY --from=asterisk-builder /var/lib/asterisk /var/lib/asterisk
COPY --from=asterisk-builder /etc/asterisk /etc/asterisk

# Copy Asterisk shared libraries
COPY --from=asterisk-builder /usr/lib/libasterisk*.so* /usr/lib/

# Copy healthcheck script
COPY healthcheck.sh /usr/local/bin/healthcheck.sh
RUN chmod +x /usr/local/bin/healthcheck.sh

# Copy entrypoint script (PUID/PGID volume permission handling)
COPY entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh

# Where this image's source is: the tarball, its checksum and the recipe commit (GPLv2 source offer), and the
# recipe's MIT notice.
COPY SOURCE.txt NOTICE /usr/share/doc/verbara-asterisk-base/
RUN echo "${RECIPE_REVISION}" | grep -Eq '^[0-9a-f]{40}$' \
        || { echo "RECIPE_REVISION '${RECIPE_REVISION}' is not a full commit id" >&2; exit 1; } \
    && ASTERISK_VERSION="${ASTERISK_VERSION}" TARBALL_SHA256="${TARBALL_SHA256}" \
       RECIPE_REVISION="${RECIPE_REVISION}" SOURCE_REPOSITORY="${SOURCE_REPOSITORY}" \
       envsubst '${ASTERISK_VERSION} ${TARBALL_SHA256} ${RECIPE_REVISION} ${SOURCE_REPOSITORY}' \
       < /usr/share/doc/verbara-asterisk-base/SOURCE.txt > /tmp/SOURCE.txt \
    && mv /tmp/SOURCE.txt /usr/share/doc/verbara-asterisk-base/SOURCE.txt \
    && chmod 644 /usr/share/doc/verbara-asterisk-base/SOURCE.txt /usr/share/doc/verbara-asterisk-base/NOTICE

# Set proper ownership and permissions
RUN chown -R asterisk:asterisk \
    /etc/asterisk \
    /home/asterisk \
    /var/*/asterisk \
    /usr/lib*/asterisk 2>/dev/null || true \
    && chmod -R 750 /var/spool/asterisk

# Runtime verification
RUN asterisk -V || (echo "Error: Unable to run asterisk -V" && exit 1)

# Define volumes for persistent data
VOLUME ["/var/lib/asterisk/sounds", "/var/lib/asterisk/keys", "/var/lib/asterisk/phoneprov", "/var/spool/asterisk", "/var/log/asterisk", "/etc/asterisk"]

# Add health check
HEALTHCHECK --interval=30s \
    --timeout=10s \
    --start-period=30s \
    --retries=3 \
    CMD /usr/local/bin/healthcheck.sh

# Entrypoint runs as root, adapts asterisk uid/gid to PUID/PGID, then execs CMD.
# Asterisk drops privileges itself via `-U asterisk -p` in the CMD below.
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]

# Set working directory
WORKDIR /home/asterisk

# Default command with production settings
CMD ["/usr/sbin/asterisk", "-vvvdddf", "-T", "-W", "-U", "asterisk", "-p"]
