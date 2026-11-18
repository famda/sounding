FROM python:3.15.2-slim
RUN pip install --no-cache-dir --root-user-action=ignore "psycopg[binary]==3.3.8" redis==8.1.0
COPY --from=famda/sounding:agent-latest /opt/sounding /opt/sounding
COPY api.py /app/api.py
USER nobody
ENTRYPOINT ["/opt/sounding/run", "python", "-u", "/app/api.py"]
