FROM nginx:1.31.8-alpine
COPY --from=famda/sounding:agent-latest /opt/sounding /opt/sounding
ENTRYPOINT ["/opt/sounding/run", "/docker-entrypoint.sh"]
CMD ["nginx", "-g", "daemon off;"]
