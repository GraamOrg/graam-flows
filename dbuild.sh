#!/bin/bash
# Build, push, and optionally deploy graam-flows Docker image
# Usage:
#   ./dbuild.sh [version] [--deploy] [--no-cache]
#   ./dbuild.sh latest --deploy     # Build, push, and deploy
#   ./dbuild.sh v1.0.0               # Build and push only
#   ./dbuild.sh latest --no-cache    # Build without cache

set -e

# Parse arguments
VERSION="latest"
DEPLOY_FLAG=""
NO_CACHE_FLAG=""

for arg in "$@"; do
    case $arg in
        --deploy)
            DEPLOY_FLAG="--deploy"
            ;;
        --no-cache)
            NO_CACHE_FLAG="--no-cache"
            ;;
        *)
            VERSION="$arg"
            ;;
    esac
done

DOCKER_USERNAME="${DOCKER_USERNAME:-rusted26}"
IMAGE_NAME="graam-flows"
FULL_IMAGE="${DOCKER_USERNAME}/${IMAGE_NAME}:${VERSION}"

echo "Building Docker image: $FULL_IMAGE"
echo "   Platforms: linux/amd64, linux/arm64"

# Check if buildx builder exists, create if not
if ! docker buildx ls | grep -q graam-builder; then
    echo "Creating buildx builder..."
    docker buildx create --name graam-builder --use
fi

# Use existing builder
docker buildx use graam-builder

# Build and push for multiple platforms
if [ -n "$NO_CACHE_FLAG" ]; then
    echo "Building and pushing to Docker Hub (no cache - clean build)..."
    docker buildx build \
      --platform linux/amd64,linux/arm64 \
      --no-cache \
      -t "$FULL_IMAGE" \
      -f Dockerfile \
      --push \
      .
else
    echo "Building and pushing to Docker Hub..."
    docker buildx build \
      --platform linux/amd64,linux/arm64 \
      -t "$FULL_IMAGE" \
      -f Dockerfile \
      --push \
      .
fi

# Tag as 'latest' using imagetools if version was specified
if [ "$VERSION" != "latest" ]; then
    LATEST_IMAGE="${DOCKER_USERNAME}/${IMAGE_NAME}:latest"
    echo "Tagging as latest..."
    docker buildx imagetools create -t "$LATEST_IMAGE" "$FULL_IMAGE"
fi

echo ""
echo "Build and push complete!"
echo ""
echo "Image: $FULL_IMAGE"
echo "Platforms: linux/amd64, linux/arm64"
echo ""

# Create git tag for build tracking
if [ "$VERSION" != "latest" ]; then
    TIMESTAMP=$(date +%Y%m%d-%H%M%S)
    TAG_NAME="build/flows/${VERSION}-${TIMESTAMP}"
    COMMIT_SHA=$(git rev-parse --short HEAD)
    BRANCH=$(git rev-parse --abbrev-ref HEAD)

    echo "Creating build tag: $TAG_NAME"

    # Create annotated tag with build metadata
    git tag -a "$TAG_NAME" -m "Build: graam-flows ${VERSION}

Docker Image: ${FULL_IMAGE}
Commit: ${COMMIT_SHA}
Branch: ${BRANCH}
Timestamp: ${TIMESTAMP}
Platforms: linux/amd64, linux/arm64"

    echo "   Pushing tag to remote..."
    if git push origin "$TAG_NAME" 2>/dev/null; then
        echo "   Tag pushed successfully"
    else
        echo "   Warning: Could not push tag (continuing anyway)"
    fi
    echo ""
fi

# Deploy if --deploy flag is provided
if [ -n "$DEPLOY_FLAG" ]; then
    echo "Deploying to production..."
    echo ""

    # Stop and remove existing container if it exists
    if docker ps -a | grep -q graam-flows; then
        echo "Stopping existing container..."
        docker stop graam-flows || true
        docker rm graam-flows || true
    fi

    # Pull the latest image
    echo "Pulling latest image..."
    docker pull "$FULL_IMAGE"

    # Run the container
    echo "Starting container..."
    docker run -d \
        --name graam-flows \
        --restart unless-stopped \
        -p 5200:5200 \
        -e ASPNETCORE_ENVIRONMENT=Production \
        -e PORT=5200 \
        "$FULL_IMAGE"

    echo ""
    echo "Deployment complete!"
    echo ""
    echo "Container status:"
    docker ps | grep graam-flows
    echo ""
    echo "View logs with: docker logs -f graam-flows"
else
    echo "To deploy, run:"
    echo "   ./dbuild.sh $VERSION --deploy"
    echo ""
    echo "Or manually on VM:"
    echo "   docker pull $FULL_IMAGE"
    echo "   docker run -d --name graam-flows -p 5200:5200 $FULL_IMAGE"
    echo ""
fi
