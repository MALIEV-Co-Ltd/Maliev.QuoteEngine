FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /
COPY nuget.config ./
COPY ["Maliev.QuoteEngine.Bff/Maliev.QuoteEngine.Bff.csproj", "Maliev.QuoteEngine.Bff/"]
COPY ["Maliev.QuoteEngine.Client/Maliev.QuoteEngine.Client.csproj", "Maliev.QuoteEngine.Client/"]
COPY ["Maliev.QuoteEngine.Shared/Maliev.QuoteEngine.Shared.csproj", "Maliev.QuoteEngine.Shared/"]

RUN --mount=type=secret,id=nuget_username \
    --mount=type=secret,id=nuget_password \
    NUGET_USERNAME=$(cat /run/secrets/nuget_username) \
    NUGET_PASSWORD=$(cat /run/secrets/nuget_password) \
    dotnet restore "Maliev.QuoteEngine.Bff/Maliev.QuoteEngine.Bff.csproj" /p:GITHUB_ACTIONS=true

COPY . .
WORKDIR "/Maliev.QuoteEngine.Bff"
RUN dotnet publish -c Release -o /app/publish --no-restore /p:GITHUB_ACTIONS=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
RUN chown -R app:app /app
USER app
COPY --from=build /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
HEALTHCHECK CMD curl -f http://localhost:8080/quote/liveness || exit 1
ENTRYPOINT ["dotnet", "Maliev.QuoteEngine.Bff.dll"]
