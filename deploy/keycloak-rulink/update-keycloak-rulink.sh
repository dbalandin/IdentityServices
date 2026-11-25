#!/usr/bin/env bash
##########################################################
# Update and restart {servicename}
# Author: oblakotech 
# Contact: developers@rulink.io
# Last edit: 2026111601
##########################################################

set -e

CONTAINERNAME="keycloak-rulink"
CONTAINERUSER="1000"
ROOT_DIR="/opt/services"
SCRIPT_DIR="${ROOT_DIR}/z-config"
CONFIG_DIR="${SCRIPT_DIR}/${CONTAINERNAME}"
SERVICE_DIR="${ROOT_DIR}/${CONTAINERNAME}"
COMPOSE_DIR="rulink-compose"
 
echo "Update and restart ${CONTAINERNAME}"
echo "Make services resources"
sudo mkdir -p "${ROOT_DIR}"
sudo mkdir -p "${SERVICE_DIR}"

echo "Copy settings to resources folder"
sudo cp -r "${CONFIG_DIR}/." "${SERVICE_DIR}/"
echo "-------------------------------"

echo "Set owner to ${SERVICE_DIR} at end"
sudo chown -R "${CONTAINERUSER}":"${CONTAINERUSER}" "${SERVICE_DIR}"
sudo chmod -R 755 "${SERVICE_DIR}"
echo "-------------------------------"


cd "${ROOT_DIR}/${COMPOSE_DIR}"
CONTAINERID=$(docker ps -aqf "name=${CONTAINERNAME}")
echo "ContainerID: ${CONTAINERID}"
echo "------------------------------"

echo "Restart service ${CONTAINERNAME}. Wait 6 sec"
sudo docker restart "${CONTAINERID}"
CONTAINERID=$(docker ps -aqf "name=${CONTAINERNAME}")
echo "ContainerID: ${CONTAINERID}"
echo "------------------------------"

sleep 6
echo "Service restarted"
sudo docker compose ps
sudo docker compose logs ${CONTAINERNAME} --tail=30
echo "------------------------------"


echo "Script completed"
echo "-------------------------------"
echo ""
