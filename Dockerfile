FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY nuget.config ./
COPY src/ src/
RUN dotnet publish src/Ledger.Api/Ledger.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# tzdata: store time zones are IANA names (Asia/Kolkata, America/New_York ...). curl: the compose health check.
RUN apt-get update && apt-get install -y --no-install-recommends tzdata curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Ledger.Api.dll"]
