FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /source
COPY global.json ./
COPY src/Bot/Bot.csproj src/Bot/packages.lock.json src/Bot/
RUN dotnet restore src/Bot --locked-mode
COPY src/Bot/ src/Bot/
RUN dotnet publish src/Bot -c Release --no-restore -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12 AS runtime
WORKDIR /app
COPY --from=build /app ./
ENV PORT=8080
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Bot.dll"]
CMD ["serve"]
