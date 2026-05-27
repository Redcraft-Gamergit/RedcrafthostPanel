FROM node:22-alpine AS frontend
WORKDIR /src/frontend
COPY frontend/package*.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS backend
WORKDIR /src
COPY backend/GameHostPanel.Api.csproj backend/
RUN dotnet restore backend/GameHostPanel.Api.csproj
COPY backend/ backend/
COPY --from=frontend /src/frontend/dist frontend/dist
RUN dotnet publish backend/GameHostPanel.Api.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine
WORKDIR /app/backend
RUN apk add --no-cache icu-libs
COPY --from=backend /out ./
COPY --from=frontend /src/frontend/dist ../frontend/dist
EXPOSE 8080
ENTRYPOINT ["dotnet", "GameHostPanel.Api.dll"]
