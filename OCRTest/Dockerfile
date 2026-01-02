# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj and restore dependencies
COPY ["OCRTest/OCRTest.csproj", "OCRTest/"]
RUN dotnet restore "OCRTest/OCRTest.csproj"

# Copy source code
COPY . .
WORKDIR "/src/OCRTest"

# Build application
RUN dotnet build "OCRTest.csproj" -c Release -o /app/build

# Stage 2: Publish
FROM build AS publish
RUN dotnet publish "OCRTest.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Create directories for file storage
RUN mkdir -p /app/data/receipts

# Copy published app
COPY --from=publish /app/publish .

# Expose ports
EXPOSE 8080

# Environment variables (overridden by docker-compose)
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "OCRTest.dll"]
