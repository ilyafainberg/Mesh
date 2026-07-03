FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Multi-project layout staged by sync-deploy.ps1: Mesh.Relay references ..\Mesh.Shared.
COPY Mesh.Shared/ Mesh.Shared/
COPY Mesh.Relay/ Mesh.Relay/
RUN dotnet restore Mesh.Relay/Mesh.Relay.csproj
RUN dotnet publish Mesh.Relay/Mesh.Relay.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Mesh.Relay.dll"]
