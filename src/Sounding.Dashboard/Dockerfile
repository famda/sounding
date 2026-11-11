FROM node:24-alpine AS client
WORKDIR /client
COPY src/Sounding.Dashboard/ClientApp/package.json src/Sounding.Dashboard/ClientApp/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY src/Sounding.Dashboard/ClientApp/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:11.0.100 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Sounding.Shared/Sounding.Shared.csproj src/Sounding.Shared/
COPY src/Sounding.Dashboard/Sounding.Dashboard.csproj src/Sounding.Dashboard/
RUN dotnet restore src/Sounding.Dashboard/Sounding.Dashboard.csproj
COPY src/Sounding.Shared/ src/Sounding.Shared/
COPY src/Sounding.Dashboard/ src/Sounding.Dashboard/
COPY --from=client /client/dist/ src/Sounding.Dashboard/wwwroot/
RUN dotnet publish src/Sounding.Dashboard/Sounding.Dashboard.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Sounding.Dashboard.dll"]
