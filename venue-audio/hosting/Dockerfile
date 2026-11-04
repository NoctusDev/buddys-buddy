FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY native/VenueAudio.Core/ native/VenueAudio.Core/
COPY native/VenueAudio.WebRelay/ native/VenueAudio.WebRelay/
RUN dotnet publish native/VenueAudio.WebRelay/VenueAudio.WebRelay.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/ ./
ENV ASPNETCORE_URLS=http://+:10000
EXPOSE 10000
USER $APP_UID
ENTRYPOINT ["dotnet", "VenueAudio.WebRelay.dll"]
