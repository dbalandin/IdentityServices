#!/usr/bin/env bash
##########################################################
# Pull {servicename} image from registry
# Author: oblakotech 
# Contact: developers@rulink.io
# Last edit: 2026111601
##########################################################

set -e

IMAGEPATH="quay.io/keycloak/keycloak:26.7.5"
CONTAINERNAME="keycloak-rulink:26.7.5"

sudo docker pull $IMAGEPATH
sudo docker tag $IMAGEPATH $CONTAINERNAME
echo "Image has been pulled"
echo "-------------------------------"

echo "List images"
sudo docker images | grep "${CONTAINERNAME}"
echo "-------------------------------"
echo "Script completed"
echo "-------------------------------"
echo ""
echo "Next steps:"
echo "Set Environments: sudo ./12-keycloak-rulink_deploy.sh"
echo "Script completed"
echo "-------------------------------"
echo ""
