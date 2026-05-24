-- WARNING: This schema is for context only and is not meant to be run.
-- Table order and constraints may not be valid for execution.

CREATE TABLE public.DeviceDrives (
  Id uuid NOT NULL,
  DeviceEntityId uuid NOT NULL,
  DriveLetter text NOT NULL,
  DriveType text NOT NULL,
  FileSystem text NOT NULL,
  VolumeLabel text NOT NULL,
  TotalSize text NOT NULL,
  UsedSpace text NOT NULL,
  FreeSpace text NOT NULL,
  CONSTRAINT DeviceDrives_pkey PRIMARY KEY (Id),
  CONSTRAINT DeviceDrives_DeviceEntityId_fkey FOREIGN KEY (DeviceEntityId) REFERENCES public.Devices(Id)
);
CREATE TABLE public.Devices (
  Id uuid NOT NULL,
  UserId uuid NOT NULL,
  DeviceName text NOT NULL,
  Processor text NOT NULL,
  ProcessorSpeed text NOT NULL,
  InstalledRam text NOT NULL,
  UsableRam text NOT NULL,
  GraphicsCard text NOT NULL,
  GraphicsMemory text NOT NULL,
  TotalStorage text NOT NULL,
  UsedStorage text NOT NULL,
  FreeStorage text NOT NULL,
  DeviceId text NOT NULL,
  ProductId text NOT NULL,
  SystemType text NOT NULL,
  WindowsEdition text NOT NULL,
  WindowsVersion text NOT NULL,
  OsBuild text NOT NULL,
  InstalledOn text NOT NULL,
  Status text NOT NULL,
  LastSeenAt text NOT NULL,
  CONSTRAINT Devices_pkey PRIMARY KEY (Id),
  CONSTRAINT Devices_UserId_fkey FOREIGN KEY (UserId) REFERENCES public.Users(Id)
);
CREATE TABLE public.Users (
  Id uuid NOT NULL,
  Email text NOT NULL UNIQUE,
  PasswordHash text NOT NULL,
  CreatedAt timestamp without time zone NOT NULL,
  CONSTRAINT Users_pkey PRIMARY KEY (Id)
);
CREATE TABLE public.__EFMigrationsHistory (
  MigrationId character varying NOT NULL,
  ProductVersion character varying NOT NULL,
  CONSTRAINT __EFMigrationsHistory_pkey PRIMARY KEY (MigrationId)
);
CREATE TABLE public.app_users (
  id uuid NOT NULL DEFAULT gen_random_uuid(),
  email text NOT NULL UNIQUE,
  password_hash text NOT NULL,
  token text NOT NULL UNIQUE,
  agent_setup_code text NOT NULL UNIQUE,
  created_at timestamp with time zone DEFAULT now(),
  CONSTRAINT app_users_pkey PRIMARY KEY (id)
);
CREATE TABLE public.devices (
  id uuid NOT NULL DEFAULT gen_random_uuid(),
  user_id uuid,
  device_name text,
  processor text,
  processor_speed text,
  installed_ram text,
  usable_ram text,
  graphics_card text,
  graphics_memory text,
  total_storage text,
  used_storage text,
  free_storage text,
  device_id text,
  product_id text,
  system_type text,
  windows_edition text,
  windows_version text,
  os_build text,
  installed_on text,
  status text,
  last_seen_at text,
  drives_json jsonb,
  created_at timestamp with time zone DEFAULT now(),
  updated_at timestamp with time zone DEFAULT now(),
  CONSTRAINT devices_pkey PRIMARY KEY (id),
  CONSTRAINT devices_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.app_users(id)
);
