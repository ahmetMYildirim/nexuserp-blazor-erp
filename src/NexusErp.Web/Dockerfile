FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080

# QuestPDF ve Türkçe karakterler (ş, ğ, İ, ı) için font desteği.
# Bu olmadan PDF'te Türkçe harfler kutu olarak çıkar.
RUN apt-get update && apt-get install -y --no-install-recommends \
        fontconfig fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# ⚠️ Katman sırası bilinçli: önce SADECE .csproj dosyaları kopyalanıp restore edilir.
# Kod değiştiğinde NuGet katmanı önbellekten gelir ve build 3 kat hızlanır.
COPY ["Directory.Build.props", "./"]
COPY ["src/NexusErp.Domain/NexusErp.Domain.csproj", "src/NexusErp.Domain/"]
COPY ["src/NexusErp.Application/NexusErp.Application.csproj", "src/NexusErp.Application/"]
COPY ["src/NexusErp.Infrastructure/NexusErp.Infrastructure.csproj", "src/NexusErp.Infrastructure/"]
COPY ["src/NexusErp.Web/NexusErp.Web.csproj", "src/NexusErp.Web/"]
RUN dotnet restore "src/NexusErp.Web/NexusErp.Web.csproj"

COPY . .
RUN dotnet publish "src/NexusErp.Web/NexusErp.Web.csproj" \
    -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "NexusErp.Web.dll"]
