# Multi-stage build for graam-flows (.NET 10 API)

# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build

WORKDIR /src

# Copy project files first (for layer caching)
COPY src/GraamFlows.Objects/GraamFlows.Objects.csproj GraamFlows.Objects/
COPY src/GraamFlows.Util/GraamFlows.Util.csproj GraamFlows.Util/
COPY src/GraamFlows.Domain/GraamFlows.Domain.csproj GraamFlows.Domain/
COPY src/GraamFlows.Core/GraamFlows.Core.csproj GraamFlows.Core/
COPY src/GraamFlows.Api/GraamFlows.Api.csproj GraamFlows.Api/

# Restore dependencies (cached unless csproj files change)
RUN dotnet restore GraamFlows.Api/GraamFlows.Api.csproj

# Copy source code
COPY src/GraamFlows.Objects/ GraamFlows.Objects/
COPY src/GraamFlows.Util/ GraamFlows.Util/
COPY src/GraamFlows.Domain/ GraamFlows.Domain/
COPY src/GraamFlows.Core/ GraamFlows.Core/
COPY src/GraamFlows.Api/ GraamFlows.Api/

# Build and publish
RUN dotnet publish GraamFlows.Api/GraamFlows.Api.csproj \
    -c Release \
    -o /app/publish

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS runtime

WORKDIR /app

# Install curl for health checks
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# Create non-root user for security
RUN groupadd --system --gid 1001 dotnet && \
    useradd --system --uid 1001 --gid dotnet appuser

# Copy published app from build stage
COPY --from=build /app/publish .

# Change ownership to non-root user
RUN chown -R appuser:dotnet /app

# Switch to non-root user
USER appuser

# Set environment variables
ENV PORT=5200
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_RUNNING_IN_CONTAINER=true

# Expose port
EXPOSE 5200

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:5200/health || exit 1

# Start the application
ENTRYPOINT ["dotnet", "GraamFlows.Api.dll"]
