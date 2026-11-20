FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/ClinicQueue.Shared/ClinicQueue.Shared.csproj src/ClinicQueue.Shared/
COPY src/ClinicQueue.Queue.Worker/ClinicQueue.Queue.Worker.csproj src/ClinicQueue.Queue.Worker/
RUN dotnet restore src/ClinicQueue.Queue.Worker/ClinicQueue.Queue.Worker.csproj

COPY src/ src/
RUN dotnet publish src/ClinicQueue.Queue.Worker/ClinicQueue.Queue.Worker.csproj \
    --configuration Release --no-restore --output /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS runtime
RUN apk add --no-cache tzdata
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ClinicQueue.Queue.Worker.dll"]
