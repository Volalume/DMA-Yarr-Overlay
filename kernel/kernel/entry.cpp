#include "library/globals.h"

NTSTATUS drv_main(PDRIVER_OBJECT driver_obj, PUNICODE_STRING registery_path)
{
	NOSCREEN_LOG("drv_main begin: driver=%p registry=%p", driver_obj, registery_path);
	///////////////////////////////
	UNREFERENCED_PARAMETER(registery_path);
	///////////////////////////////

	///////////////////////////////
	PDEVICE_OBJECT dev_obj;
	NTSTATUS status = STATUS_SUCCESS;
	///////////////////////////////

	///////////////////////////////
	status = init_function();
	NOSCREEN_LOG("init_function returned 0x%08X", status);
	if (!NT_SUCCESS(status))
		return status;
	///////////////////////////////

	///////////////////////////////
	UNICODE_STRING dev_name, sym_link;

	dev_name = ansi_to_unicode("\\Device\\NoScreen");
	sym_link = ansi_to_unicode("\\DosDevices\\NoScreen");
	///////////////////////////////

	///////////////////////////////
	status = IoCreateDevice(driver_obj, 0, &dev_name, file_device_mirrore, 0x00000100, 0, &dev_obj);
	NOSCREEN_LOG("IoCreateDevice name=%wZ type=0x%X status=0x%08X device=%p", &dev_name, file_device_mirrore, status, NT_SUCCESS(status) ? dev_obj : nullptr);
	if (!NT_SUCCESS(status))
		return status;
	///////////////////////////////

	///////////////////////////////
	status = IoCreateSymbolicLink(&sym_link, &dev_name);
	NOSCREEN_LOG("IoCreateSymbolicLink link=%wZ target=%wZ status=0x%08X", &sym_link, &dev_name, status);
	if (!NT_SUCCESS(status))
	{
		IoDeleteDevice(dev_obj);
		return status;
	}
	///////////////////////////////

	///////////////////////////////
	SetFlag(dev_obj->Flags, DO_BUFFERED_IO);

	for (int t = 0; t <= IRP_MJ_MAXIMUM_FUNCTION; t++)
		driver_obj->MajorFunction[t] = unsupported_io;

	driver_obj->MajorFunction[IRP_MJ_CREATE] = create_io;
	driver_obj->MajorFunction[IRP_MJ_CLOSE] = close_io;
	driver_obj->MajorFunction[IRP_MJ_DEVICE_CONTROL] = ctl_io;
	driver_obj->DriverUnload = NULL;

	ClearFlag(dev_obj->Flags, DO_DEVICE_INITIALIZING);
	NOSCREEN_LOG("drv_main ready: device=%p flags=0x%08X", dev_obj, dev_obj->Flags);
	///////////////////////////////

	return status;
}

NTSTATUS entry_point(PVOID a1, PVOID a2)
{
	NOSCREEN_LOG("entry_point begin: a1=%p a2=%p", a1, a2);
	auto driver_name = ansi_to_unicode("\\Driver\\NoScreen");
	const auto status = IoCreateDriver(&driver_name, &drv_main);
	NOSCREEN_LOG("IoCreateDriver name=%wZ returned 0x%08X", &driver_name, status);
	RtlFreeUnicodeString(&driver_name);
	return status;
}
