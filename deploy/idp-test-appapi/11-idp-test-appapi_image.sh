#!/usr/bin/env bash
##########################################################
# Pull idp-test-appapi (demo-api) image from Yandex Registry
# Author: oblakotech
# Contact: developers@rulink.io
# Last edit: 2026111701
##########################################################

set -e

IMAGEPATH="cr.yandex/crp9et2dv05vocv8pagt/idptestappapi"
CONTAINERNAME="idptestappapi"

echo "Get IAM token for Yandex Container Registry before pull image"
echo "execute: yc iam create-token on admin ws and copy token"
read -p 'Enter token: ' TOKEN
export TOKEN
echo "-------------------------------"
echo "${TOKEN}" | docker login --username iam --password-stdin cr.yandex
echo "Pull image from registry"
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
echo "Set Environments: sudo ./12-idp-test-appapi_deploy.sh"
echo "Script completed"
echo "-------------------------------"
echo ""
