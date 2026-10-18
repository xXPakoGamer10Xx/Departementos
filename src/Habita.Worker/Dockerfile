# Build from the repository root:  docker build -f src/Habita.Worker/Dockerfile -t habita-worker .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Habita.Domain/Habita.Domain.csproj src/Habita.Domain/
COPY src/Habita.Application/Habita.Application.csproj src/Habita.Application/
COPY src/Habita.Infrastructure/Habita.Infrastructure.csproj src/Habita.Infrastructure/
COPY src/Habita.Worker/Habita.Worker.csproj src/Habita.Worker/
RUN dotnet restore src/Habita.Worker/Habita.Worker.csproj
COPY src/ src/
RUN dotnet publish src/Habita.Worker/Habita.Worker.csproj -c Release -o /app --no-restore -p:UseAppHost=false

# The data layer references the ASP.NET Core shared framework, so the worker runs on the same runtime image as the API (it just never opens a port).
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV DOTNET_ENVIRONMENT=Production DOTNET_EnableDiagnostics=0
USER $APP_UID
ENTRYPOINT ["dotnet", "Habita.Worker.dll"]
