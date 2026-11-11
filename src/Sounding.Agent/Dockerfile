# The agent bundle, /opt/sounding: the agent compiled ahead of time against musl (with musl's loader and
# OpenSSL beside it), the launcher, net-meter and gpu-meter. Add it to any image with
#   COPY --from=famda/sounding:agent-latest /opt/sounding /opt/sounding
# or run this image alone with pid: host to sample the host.

FROM alpine:3.22 AS build
RUN apk add --no-cache bash curl ca-certificates clang build-base linux-headers zlib-dev libstdc++ \
    && curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --version 11.0.100 --install-dir /usr/share/dotnet
ENV PATH=/usr/share/dotnet:$PATH DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Sounding.Shared/Sounding.Shared.csproj src/Sounding.Shared/
COPY src/Sounding.Agent/Sounding.Agent.csproj src/Sounding.Agent/
RUN dotnet restore src/Sounding.Agent/Sounding.Agent.csproj -r linux-musl-x64
COPY src/Sounding.Shared/ src/Sounding.Shared/
COPY src/Sounding.Agent/ src/Sounding.Agent/
RUN dotnet publish src/Sounding.Agent/Sounding.Agent.csproj -c Release -r linux-musl-x64 --no-restore -o /opt/sounding/agent \
    && cd /opt/sounding/agent \
    && mv Sounding.Agent sounding-agent.elf && rm -f *.dbg *.pdb \
    && cp -L /lib/ld-musl-x86_64.so.1 sounding-agent \
    && cp -L /usr/lib/libssl.so.3 /usr/lib/libcrypto.so.3 . \
    && gcc -O2 -s -Wall -static -o /opt/sounding/net-meter /src/src/Sounding.Agent/Native/netmeter.c \
    && gcc -Os -s -Wall -static -o /opt/sounding/run /src/src/Sounding.Agent/Native/launcher.c

FROM nvidia/cuda:12.6.3-devel-ubuntu24.04 AS gpu-meter
WORKDIR /src
COPY src/Sounding.Agent/Native/gpumeter.c ./
RUN gcc -O2 -s -fPIC -shared -o libgpumeter.so gpumeter.c \
        -I/usr/local/cuda/include -L/usr/local/cuda/lib64 -L/usr/local/cuda/lib64/stubs \
        -lcupti -lcuda -lpthread -Wl,-rpath,'$ORIGIN' \
    && cp -L /usr/local/cuda/lib64/libcupti.so.12 .

# glibc base, for the nvidia-smi the NVIDIA runtime injects when the image runs alone on a GPU host.
FROM ubuntu:24.04
COPY --from=build /opt/sounding /opt/sounding
COPY --from=gpu-meter /src/libgpumeter.so /src/libcupti.so.12 /opt/sounding/
RUN apt-get update && apt-get install -y --no-install-recommends libcap2-bin \
    && setcap cap_net_raw=ep /opt/sounding/net-meter \
    && apt-get purge -y libcap2-bin && apt-get autoremove -y && rm -rf /var/lib/apt/lists/*
USER ubuntu
ENTRYPOINT ["/opt/sounding/run"]
