# Build from the repository root:  docker build -f src/Habita.Api/Dockerfile -t habita-api .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Habita.Domain/Habita.Domain.csproj src/Habita.Domain/
COPY src/Habita.Application/Habita.Application.csproj src/Habita.Application/
COPY src/Habita.Infrastructure/Habita.Infrastructure.csproj src/Habita.Infrastructure/
COPY src/Habita.Api/Habita.Api.csproj src/Habita.Api/
RUN dotnet restore src/Habita.Api/Habita.Api.csproj
COPY src/ src/
RUN dotnet publish src/Habita.Api/Habita.Api.csproj -c Release -o /app --no-restore -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 ASPNETCORE_ENVIRONMENT=Production DOTNET_EnableDiagnostics=0
EXPOSE 8080
# The data-protection key folder must belong to the unprivileged user, or sessions cannot be encrypted. A named volume inherits this ownership.
RUN mkdir /keys /data /data/files && chown $APP_UID:$APP_UID /keys /data/files
USER $APP_UID
ENTRYPOINT ["dotnet", "Habita.Api.dll"]
