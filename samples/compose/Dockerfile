# Each sample passes its own BASE; the default only keeps the image name valid.
ARG BASE=busybox:1.37
FROM ${BASE}
COPY --from=famda/sounding:agent-latest /opt/sounding /opt/sounding
