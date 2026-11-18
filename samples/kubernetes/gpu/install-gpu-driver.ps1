<#
.SYNOPSIS
Copies the NVIDIA driver's WSL files onto each node of Docker Desktop's Kubernetes cluster.

.DESCRIPTION
Docker Desktop adds the driver to containers started with --gpus, but not to its Kubernetes nodes. This copies those
files (libcuda, NVML, the driver folder and nvidia-smi) to /opt/wsl-gpu on every node, for node-agent.yaml to mount.
Run it again after a driver update or a cluster reset.
#>
#Requires -Version 7.4
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$nodes = kubectl get nodes -o 'jsonpath={.items[*].metadata.name}'
if (-not $nodes) { throw 'No Kubernetes nodes: enable Kubernetes in Docker Desktop first.' }

# Runs in a container that has the GPU, where Docker puts the driver's files.
$copy = @'
set -e
mkdir -p /out/lib /out/bin /out/drivers
for f in libcuda.so.1 libnvidia-ml.so.1 libcudadebugger.so.1 libnvidia-gpucomp.so libdxcore.so; do cp -L /usr/lib64/$f /out/lib/; done
ln -sf libcuda.so.1 /out/lib/libcuda.so
ln -sf libnvidia-ml.so.1 /out/lib/libnvidia-ml.so
cp -rL /usr/lib/wsl/drivers/. /out/drivers/
cp -L /usr/bin/nvidia-smi /out/bin/
tar c -C /out .
'@

foreach ($node in $nodes -split ' ') {
    # docker exec reaches the node containers by name, though docker ps doesn't list them. PowerShell 7.4 passes the
    # archive's bytes through the pipe unchanged.
    docker run --rm --gpus all busybox:1.37 sh -c $copy |
        docker exec -i $node sh -c 'rm -rf /opt/wsl-gpu && mkdir -p /opt/wsl-gpu && tar x -C /opt/wsl-gpu'
    Write-Host "$node`: $(docker exec $node du -sh /opt/wsl-gpu)"
}
