#pragma once
#include <ntdef.h>
#include <ntifs.h>
#include <ntddk.h>
#include <ntstrsafe.h>
#include <minwindef.h>
#include <ifdef.h>
#include <ntddndis.h>
#include <wdm.h>
#include <intrin.h>
#include <ntimage.h>

#define NOSCREEN_LOG(format, ...) \
	DbgPrintEx(DPFLTR_IHVDRIVER_ID, DPFLTR_ERROR_LEVEL, "[NoScreen] " format "\n", __VA_ARGS__)

#include "stdint.h"
#include "structs.h"
#include "utils.h"
#include "ioctl.h"
