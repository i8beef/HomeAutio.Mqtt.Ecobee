FROM microsoft/dotnet:2.1-sdk-alpine AS build
WORKDIR /app

# copy csproj and restore as distinct layers
COPY nuget.config .
COPY *.sln .
COPY HomeAutio.Mqtt.Ecobee/*.csproj ./HomeAutio.Mqtt.Ecobee/
RUN dotnet restore

# copy and build everything else
COPY HomeAutio.Mqtt.Ecobee/. ./HomeAutio.Mqtt.Ecobee/
RUN dotnet build -c Release

# publish
FROM build AS publish
WORKDIR /app/HomeAutio.Mqtt.Ecobee
RUN dotnet publish -o out

# build runtime image
FROM microsoft/dotnet:2.1-runtime-alpine AS runtime
WORKDIR /app
COPY --from=publish /app/HomeAutio.Mqtt.Ecobee/out ./

VOLUME ["/app/appsettings.json", "/app/token.txt", "/app/logs"]
ENTRYPOINT ["dotnet", "HomeAutio.Mqtt.Ecobee.dll"]