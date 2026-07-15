
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project file first for better Docker layer caching.
COPY ["RSS API.csproj", "./"]
RUN dotnet restore "RSS API.csproj"

# Copy the rest of the repository.
COPY . .

# Publish as a framework-dependent ASP.NET Core app.
RUN dotnet publish "RSS API.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_HTTP_PORTS=8080
ENV DOTNET_EnableDiagnostics=0

COPY --from=build /app/publish .

# The Substack controller loads this OPML file from ContentRootPath,
# so make sure it exists beside the published app.
COPY --from=build /src/feedbro-subscriptions-20260317-015028.opml .

EXPOSE 8080

# Official modern .NET images include a non-root app user.
USER $APP_UID

ENTRYPOINT ["dotnet", "RSS API.dll"]