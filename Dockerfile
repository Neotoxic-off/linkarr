# Base images are pinned by digest so a rebuild cannot silently pick up a different image; bump the digests to take updates.
FROM node:24-alpine@sha256:06d7adf3e0ef61a13f1d28682726366f97a15aa1888c934d3ecec86c33918b53 AS web
WORKDIR /web
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2 AS api
WORKDIR /src
COPY backend/ ./
RUN dotnet publish src/Linkarr.Api -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:8af87eba5e7cdafb1514d7388be287365da97c132509e84dfe252ffd552398ca
WORKDIR /app
COPY --from=api /app ./
COPY --from=web /web/out ./wwwroot
# linkarr.db and the Data Protection key ring (which encrypts the stored API keys) live here; mount it as a volume.
ENV LINKARR_CONFIG_DIR=/config \
    ASPNETCORE_HTTP_PORTS=8070
RUN mkdir /config && chown 1000:1000 /config && chmod 0700 /config
VOLUME /config
# Not root: uid/gid 1000, like visularr, owning only /config.
USER 1000:1000
EXPOSE 8070
ENTRYPOINT ["dotnet", "Linkarr.Api.dll"]
