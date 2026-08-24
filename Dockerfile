# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

COPY NuGet.config global.json Directory.Build.props Directory.Packages.props ./
COPY BambuMCPSharp.csproj ./
# The BambuLab.X1Camera packages come from GitHub Packages, which refuses anonymous
# downloads. Pass a token with read:packages as a BuildKit secret:
#   docker build --secret id=nuget_github_token,env=GITHUB_TOKEN .
ARG TARGETARCH
RUN --mount=type=secret,id=nuget_github_token \
    arch="${TARGETARCH:-amd64}"; \
    if [ "$arch" = "amd64" ]; then arch="x64"; fi; \
    rid="linux-$arch"; \
    if [ -s /run/secrets/nuget_github_token ]; then \
    dotnet nuget update source GitHub-Wixely-Packages \
    --username token \
    --password "$(cat /run/secrets/nuget_github_token)" \
    --store-password-in-clear-text \
    --configfile NuGet.config; \
    fi; \
    dotnet restore BambuMCPSharp.csproj \
    -r "$rid" \
    -p:PublishSingleFile=true \
    -p:SelfContained=false \
    -p:EnableCompressionInSingleFile=false

COPY . .
RUN arch="${TARGETARCH:-amd64}"; \
    if [ "$arch" = "amd64" ]; then arch="x64"; fi; \
    rid="linux-$arch"; \
    dotnet publish BambuMCPSharp.csproj \
    -c Release \
    --no-restore \
    -r "$rid" \
    --self-contained false \
    -o /app/publish \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=false \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:IncludeAllContentForSelfExtract=true \
    -p:IsTransformWebConfigDisabled=true \
    -p:StaticWebAssetsEnabled=false \
    -p:DebugType=none \
    -p:DebugSymbols=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
WORKDIR /app

# The safe posture is baked in: read-only, and every dangerous category off. An operator
# who wants this container to actually touch the printer has to say so explicitly.
ENV DOTNET_ENVIRONMENT=Production \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    BAMBUMCP_Server__Host=0.0.0.0 \
    BAMBUMCP_Server__Port=5718 \
    BAMBUMCP_Server__Path=/mcp \
    BAMBUMCP_Server__Password= \
    BAMBUMCP_Bambu__ReadOnly=true \
    BAMBUMCP_Bambu__AllowStopPrint=false \
    BAMBUMCP_Bambu__AllowStartPrint=false \
    BAMBUMCP_Bambu__AllowTemperatureControl=false \
    BAMBUMCP_Bambu__AllowMotionControl=false \
    BAMBUMCP_Bambu__AllowRawGcode=false \
    BAMBUMCP_Bambu__AllowCalibration=false \
    BAMBUMCP_Bambu__AllowFileDelete=false

RUN mkdir -p /app/logs /app/transfers /app/snapshots && chown -R $APP_UID:0 /app
COPY --from=build --chown=$APP_UID:0 /app/publish ./

USER $APP_UID
EXPOSE 5718
VOLUME ["/app/logs", "/app/transfers", "/app/snapshots"]

ENTRYPOINT ["./BambuMCPSharp"]
