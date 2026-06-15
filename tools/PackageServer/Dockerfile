# Build context = this directory (tools/PackageServer). On Railway: set the service "Root Directory" to
# tools/PackageServer, attach a Volume mounted at /data, and deploy — PORT is injected automatically.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY PackageServer.csproj .
RUN dotnet restore PackageServer.csproj
COPY . .
RUN dotnet publish PackageServer.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Default data dir; override with PACKAGES_DIR. Persistence comes from a Railway Volume attached at this
# mount path (a Docker VOLUME instruction is rejected by Railway's builder, so we don't declare one).
ENV PACKAGES_DIR=/data
EXPOSE 8080
ENTRYPOINT ["dotnet", "PackageServer.dll"]
