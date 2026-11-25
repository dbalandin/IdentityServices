#!/usr/bin/env bash
##########################################################
# Set up idp-test-appweb (demopage) service
# Author: oblakotech
# Contact: developers@rulink.io
# Last edit: 2026111701
##########################################################

set -e

CONTAINERNAME="idp-test-appweb"
ROOT_DIR="/opt/services"
SCRIPT_DIR="${ROOT_DIR}/z-config"
CONFIG_DIR="${SCRIPT_DIR}/${CONTAINERNAME}"
SERVICE_DIR="${ROOT_DIR}/${CONTAINERNAME}"
RULINK_COMPOSE="${SCRIPT_DIR}/rulink-compose/compose.yaml"
RULINK_ENV="${SCRIPT_DIR}/rulink-compose/.env"
SITE_DIR="${SCRIPT_DIR}/nginx_sites"

function makeServiceDirs() {
    echo "Make services resources"
    sudo mkdir -p "${ROOT_DIR}"
    sudo mkdir -p "${SERVICE_DIR}"
    sudo mkdir -p "${CONFIG_DIR}"
    echo "-------------------------------"
    echo ""
}

function createServiceUpdateScript() {
    echo "Copy scripts to updates folder"
    sudo chmod +x *sh
    sudo cp "update-${CONTAINERNAME}.sh" "${SCRIPT_DIR}"
    echo "-------------------------------"
    echo ""
}

function copyNginxSiteConfigToConfigFolder() {
    echo "Copy nginx site config to config folder"
    sudo cp "nginx/${CONTAINERNAME}.site" "${SITE_DIR}/${CONTAINERNAME}"
    echo "-------------------------------"
    echo ""
}

function mergeComposeAndEnv() {
    echo "Merge compose.yaml"
    if grep -qE "^[[:space:]]*${CONTAINERNAME}:" "${RULINK_COMPOSE}"; then
        echo "Compose file already contains ${CONTAINERNAME} service. Skipping merge."
    else
        sudo bash -c "cat docker-compose.yml >> ${RULINK_COMPOSE}"
        echo "Merged docker-compose.yml into ${RULINK_COMPOSE}"
    fi
    echo "-------------------------------"

    echo "Merge .env (only new variables, без дублей)"
    if [ -f ".env" ]; then
        while IFS= read -r line; do
            case "$line" in
                ''|\#*) continue ;;
            esac
            VARNAME="${line%%=*}"
            if ! grep -q "^${VARNAME}=" "${RULINK_ENV}" 2>/dev/null; then
                sudo bash -c "printf '%s\n' '${line}' >> ${RULINK_ENV}"
                echo "added ${VARNAME}"
            else
                echo "skip ${VARNAME} (уже есть)"
            fi
        done < ".env"
    else
        echo ".env file does not exist. Skipping merge."
    fi
    echo "-------------------------------"
}

function checkResultFolders() {
    echo "Check created folders"
    if [ -d "${SERVICE_DIR}" ] && [ -d "${CONFIG_DIR}" ]; then
        echo "Service folders created successfully:"
        echo "Service Folder: ${SERVICE_DIR}"
        echo "Config Folder: ${CONFIG_DIR}"
    else
        echo "Error: Service folders were not created correctly."
        ls -l "${SERVICE_DIR}"
        ls -l "${CONFIG_DIR}"
        exit 1
    fi

    if [ -f "${SCRIPT_DIR}"/update-"${CONTAINERNAME}".sh ]; then
        echo "update-${CONTAINERNAME}.sh created successfully:"
        sudo ls -l "${SCRIPT_DIR}"/update-"${CONTAINERNAME}".sh
    else
        echo "Error: Service update script was not created correctly."
        exit 1
    fi
    echo "-------------------------------"
    echo ""
}

#MAIN SCRIPT EXECUTION
echo "start config ${CONTAINERNAME}"
makeServiceDirs
createServiceUpdateScript
copyNginxSiteConfigToConfigFolder
mergeComposeAndEnv
checkResultFolders
echo "Script completed"
echo ""
echo "Next steps:"
echo "Apply nginx sites: sudo /opt/services/z-config/update-nginx.sh"
echo "Restart compose:   sudo /opt/services/z-config/update-compose.sh"
echo "-------------------------------"
echo ""
