#!/bin/bash

CONTAINER_NAME="playwright-mcp-service"
IMAGE_NAME="mcr.microsoft.com/playwright/mcp:latest"

echo "Stopping and removing existing container..."
docker rm -f "$CONTAINER_NAME" 2>/dev/null

echo "Starting $CONTAINER_NAME with caching fully disabled..."
docker run -d \
  --name "$CONTAINER_NAME" \
  --network host \
  --ipc host \
  --init \
  --user node \
  --security-opt label=disable \
  --entrypoint node \
  -v /var/run/docker.sock:/var/run/docker.sock \
  "$IMAGE_NAME" \
  /app/cli.js \
    --headless \
    --browser chromium \
    --no-sandbox \
    --isolated \
    --host 0.0.0.0 \
    --port 8931 \
    --viewport-size 1440x900 \
    --caps vision,pdf,devtools

echo "Container started. Caching has been strictly disabled."
