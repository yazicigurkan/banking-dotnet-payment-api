FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY NuGet.config ./
COPY src/Payment.Api/Payment.Api.csproj src/Payment.Api/
RUN dotnet restore src/Payment.Api/Payment.Api.csproj --configfile NuGet.config
COPY . .
RUN dotnet publish src/Payment.Api/Payment.Api.csproj -c Release -o /out --no-restore

FROM runtime AS final
COPY --from=build /out .
ENTRYPOINT ["dotnet", "Payment.Api.dll"]
