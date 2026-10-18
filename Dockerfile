FROM debian:trixie

# Ensure apt is noninteractive
ENV DEBIAN_FRONTEND=noninteractive

# Install system dependencies and locales
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        locales \
        python3 \
        git \
        curl \
        wget \
        ca-certificates \
        gnupg2 \
        apt-transport-https \
        freetds-dev \
        freetds-bin \
        freetds-common \
        odbcinst \
        unixodbc \
        build-essential \
        pgloader \
        postgresql-client \
        vim \
    && sed -i 's/^# *\(en_US.UTF-8 UTF-8\)/\1/' /etc/locale.gen || true \
    && locale-gen en_US.UTF-8 \
    && echo "LANG=en_US.UTF-8" > /etc/default/locale \
    && apt-get clean && rm -rf /var/lib/apt/lists/*

# Install uv (fast Python package manager)
COPY --from=ghcr.io/astral-sh/uv:latest /uv /uvx /usr/local/bin/

# Create a venv at a fixed location (not overlaid by volume mount)
ENV UV_PROJECT_ENVIRONMENT="/opt/venv"
RUN uv venv /opt/venv

# Copy project files for dependency installation
COPY pyproject.toml uv.lock /tmp/

# Install Python dependencies with uv
WORKDIR /tmp
RUN uv sync --frozen 2>/dev/null || uv sync

# Add venv to PATH
ENV PATH="/opt/venv/bin:$PATH"

# Set actual workdir
WORKDIR /workspace

# Configure FreeTDS default client charset and TDS version
RUN mkdir -p /etc/freetds && printf "[global]\n\ttds version = 7.4\n\tclient charset = UTF-8\n" > /etc/freetds/freetds.conf

# Default environment variables useful for pgloader runs
ENV TDSVER=7.4
ENV LANG=en_US.UTF-8

# Default command: open a shell
CMD ["/bin/bash"]
