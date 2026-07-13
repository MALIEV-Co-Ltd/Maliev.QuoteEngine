# syntax=docker/dockerfile:1.7

FROM node:22-bookworm-slim@sha256:53ada149d435c38b14476cb57e4a7da73c15595aba79bd6971b547ceb6d018bf AS node

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:ea8bde36c11b6e7eec2656d0e59101d4462f6bd630730f2c8201ed0572b295d5 AS build

# The Blazor client invokes npm from its MSBuild targets. Copy only the Node
# runtime and global npm installation into the SDK stage; Node is not present in
# the final image.
COPY --from=node /usr/local/bin/ /usr/local/bin/
COPY --from=node /usr/local/lib/node_modules/ /usr/local/lib/node_modules/

WORKDIR /src

COPY Directory.Build.props nuget.config ./
COPY Maliev.QuoteEngine.Bff/Maliev.QuoteEngine.Bff.csproj Maliev.QuoteEngine.Bff/
COPY Maliev.QuoteEngine.Client/Maliev.QuoteEngine.Client.csproj Maliev.QuoteEngine.Client/
COPY Maliev.QuoteEngine.Shared/Maliev.QuoteEngine.Shared.csproj Maliev.QuoteEngine.Shared/

# GitHub Packages credentials exist only for this restore instruction. BuildKit
# never persists secret mounts in an image layer or the build cache.
RUN --mount=type=secret,id=nuget_username,required=true \
    --mount=type=secret,id=nuget_password,required=true \
    NUGET_USERNAME="$(cat /run/secrets/nuget_username)" \
    NUGET_PASSWORD="$(cat /run/secrets/nuget_password)" \
    dotnet restore Maliev.QuoteEngine.Bff/Maliev.QuoteEngine.Bff.csproj \
      --configfile nuget.config \
      --property:GITHUB_ACTIONS=true \
      --property:ContinuousIntegrationBuild=true

COPY Maliev.QuoteEngine.Bff/ Maliev.QuoteEngine.Bff/
COPY Maliev.QuoteEngine.Client/ Maliev.QuoteEngine.Client/
COPY Maliev.QuoteEngine.Shared/ Maliev.QuoteEngine.Shared/

RUN node --version \
    && npm --version \
    && dotnet publish Maliev.QuoteEngine.Bff/Maliev.QuoteEngine.Bff.csproj \
      --configuration Release \
      --no-restore \
      --output /app/publish \
      --property:GITHUB_ACTIONS=true \
      --property:ContinuousIntegrationBuild=true \
      --property:Deterministic=true \
      --property:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:7644f992230d35cf230017189d4038c0ae0f7388b13f4f7ae1900a155bafb597 AS runtime

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_RUNNING_IN_CONTAINER=true

WORKDIR /app
COPY --from=build --chown=app:app /app/publish/ ./

USER app
EXPOSE 8080

ENTRYPOINT ["dotnet", "Maliev.QuoteEngine.Bff.dll"]
