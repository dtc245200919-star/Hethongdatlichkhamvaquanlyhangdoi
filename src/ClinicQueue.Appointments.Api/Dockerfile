FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/ClinicQueue.Shared/ClinicQueue.Shared.csproj src/ClinicQueue.Shared/
COPY src/ClinicQueue.Appointments.Api/ClinicQueue.Appointments.Api.csproj src/ClinicQueue.Appointments.Api/
RUN dotnet restore src/ClinicQueue.Appointments.Api/ClinicQueue.Appointments.Api.csproj

COPY src/ src/
RUN dotnet publish src/ClinicQueue.Appointments.Api/ClinicQueue.Appointments.Api.csproj \
    --configuration Release --no-restore --output /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
RUN apk add --no-cache curl tzdata
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "ClinicQueue.Appointments.Api.dll"]
