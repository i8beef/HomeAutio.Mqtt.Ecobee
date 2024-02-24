ARG VERSION=1.0.0.0
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
ARG VERSION
WORKDIR /app

# copy csproj and restore as distinct layers
COPY nuget.config .
COPY *.sln .
COPY HomeAutio.Mqtt.Ecobee/*.csproj ./HomeAutio.Mqtt.Ecobee/
RUN dotnet restore

# copy and build everything else
COPY HomeAutio.Mqtt.Ecobee/. ./HomeAutio.Mqtt.Ecobee/
RUN dotnet build --configuration Release /p:Version=$VERSION

# publish
FROM build AS publish
WORKDIR /app/HomeAutio.Mqtt.Ecobee
RUN dotnet publish -o out

# build runtime image
FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine AS runtime
WORKDIR /app
COPY --from=publish /app/HomeAutio.Mqtt.Ecobee/out ./

ENV ENVIRONMENT Production

VOLUME ["/app/appsettings.Production.json", "/app/logs"]
ENTRYPOINT ["dotnet", "HomeAutio.Mqtt.Ecobee.dll"]