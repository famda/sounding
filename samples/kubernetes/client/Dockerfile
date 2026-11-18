FROM busybox:1.37
COPY --from=famda/sounding:agent-latest /opt/sounding /opt/sounding
ENTRYPOINT ["/opt/sounding/run"]
CMD ["sh", "-c", "while true; do wget -q -O /dev/null http://web/; sleep 2; done"]
