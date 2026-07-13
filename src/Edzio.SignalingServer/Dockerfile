# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Edzio.SignalingServer/Edzio.SignalingServer.csproj src/Edzio.SignalingServer/
RUN dotnet restore src/Edzio.SignalingServer/Edzio.SignalingServer.csproj
COPY src/Edzio.SignalingServer/ src/Edzio.SignalingServer/
RUN dotnet publish src/Edzio.SignalingServer/Edzio.SignalingServer.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Edzio.SignalingServer.dll"]
