FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src

COPY GraamFlows.sln .
COPY src/GraamFlows.Api/GraamFlows.Api.csproj src/GraamFlows.Api/
COPY src/GraamFlows.Cli/GraamFlows.Cli.csproj src/GraamFlows.Cli/
COPY src/GraamFlows.Core/GraamFlows.Core.csproj src/GraamFlows.Core/
COPY src/GraamFlows.Domain/GraamFlows.Domain.csproj src/GraamFlows.Domain/
COPY src/GraamFlows.Objects/GraamFlows.Objects.csproj src/GraamFlows.Objects/
COPY src/GraamFlows.Util/GraamFlows.Util.csproj src/GraamFlows.Util/
COPY tests/GraamFlows.Tests/GraamFlows.Tests.csproj tests/GraamFlows.Tests/
RUN dotnet restore

COPY . .
RUN dotnet publish src/GraamFlows.Api -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview
WORKDIR /app
COPY --from=build /app .

ENV PORT=5200
EXPOSE 5200

ENTRYPOINT ["dotnet", "GraamFlows.Api.dll"]
