FROM python:3.13-slim
# Only the Docker client: the runner stops, starts and limits the samples' containers through the mounted socket.
COPY --from=docker:28-cli /usr/local/bin/docker /usr/local/bin/docker
WORKDIR /evals
COPY eval.py grading.py scenarios.json ./
COPY grading/ grading/
ENTRYPOINT ["python", "-u", "eval.py"]
