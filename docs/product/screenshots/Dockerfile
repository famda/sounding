FROM mcr.microsoft.com/dotnet/sdk:11.0.100 AS seed
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Sounding.Shared/Sounding.Shared.csproj src/Sounding.Shared/
COPY docs/product/screenshots/seed/Sounding.Demo.csproj docs/product/screenshots/seed/
RUN dotnet restore docs/product/screenshots/seed/Sounding.Demo.csproj -r linux-x64
COPY src/Sounding.Shared/ src/Sounding.Shared/
COPY docs/product/screenshots/seed/ docs/product/screenshots/seed/
RUN dotnet publish docs/product/screenshots/seed/Sounding.Demo.csproj -c Release -r linux-x64 --self-contained true --no-restore -o /seed

FROM mcr.microsoft.com/playwright:v1.56.1-noble
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg
WORKDIR /capture
COPY docs/product/screenshots/package.json docs/product/screenshots/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY --from=seed /seed /seed
COPY docs/product/screenshots/*.mjs ./
COPY docs/product/screenshots/specs/ ./specs/
ENTRYPOINT ["npm", "run", "capture"]
