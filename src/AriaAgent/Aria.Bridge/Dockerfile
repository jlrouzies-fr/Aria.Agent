FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore Aria.Bridge/Aria.Bridge.csproj
RUN dotnet publish Aria.Bridge/Aria.Bridge.csproj \
    -c Release \
    --self-contained false \
    -p:PublishSingleFile=false \
    -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# The runtime image runs as the non-root 'app' user. Ensure the bridge vault directory
# (/home/app/.config/aria-bridge) exists and is writable before dropping to that user.
USER root
RUN mkdir -p /home/app/.config/aria-bridge && chown -R app:app /home/app/.config
USER app

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:5741
EXPOSE 5741
ENTRYPOINT ["dotnet", "aria-bridge.dll"]
