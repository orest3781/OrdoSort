# File mover with logging, security preservation, and atomic operations
import os
import sys
import shutil
import time
import datetime
import csv
import traceback
import configparser
import re
import logging
import atexit
import signal
from logging.handlers import RotatingFileHandler

# PDF processing and Windows security
import fitz  # PyMuPDF for PDF operations
import win32security  # Windows file security

# Windows console management
try:
    import win32gui
    import win32con
    WINDOWS_CONSOLE_AVAILABLE = True
except ImportError:
    WINDOWS_CONSOLE_AVAILABLE = False

# Determine script directory
if getattr(sys, 'frozen', False):
    script_dir = os.path.dirname(sys.executable)
else:
    try:
        script_dir = os.path.dirname(os.path.abspath(__file__))
    except NameError:
        script_dir = os.getcwd()

# Configure logging with rotation
logger = logging.getLogger('FileMoverLogger')
logger.setLevel(logging.DEBUG)

os.makedirs(script_dir, exist_ok=True)
log_file = os.path.join(script_dir, "file_mover.log")

# File handler: 5MB max, 5 backup files
file_handler = RotatingFileHandler(
    log_file, maxBytes=5*1024*1024, backupCount=5, encoding='utf-8'
)
file_handler.setLevel(logging.DEBUG)

# Console handler
console_handler = logging.StreamHandler()
console_handler.setLevel(logging.INFO)

# Format and add handlers
formatter = logging.Formatter('%(asctime)s - %(levelname)s - %(message)s')
file_handler.setFormatter(formatter)
console_handler.setFormatter(formatter)
logger.addHandler(file_handler)
logger.addHandler(console_handler)

# Helper functions

def load_config(config_file):
    """Load INI configuration file."""
    config = configparser.ConfigParser(interpolation=None)
    if not os.path.exists(config_file):
        print(f"Configuration file '{config_file}' not found.")
        sys.exit(1)
    try:
        config.read(config_file)
        return config
    except Exception as e:
        print(f"Failed to read configuration file '{config_file}'. Exception: {str(e)}")
        sys.exit(1)

def get_log_file_path(log_folder_path, log_file_name_base, log_date_format):
    """Generate daily log file path."""
    log_date = datetime.datetime.now().strftime(log_date_format)
    log_file_name = f"{log_date}-{log_file_name_base}.csv"
    return os.path.join(log_folder_path, log_file_name)

def get_daily_archive_folder(archive_folder_base, archive_folder_format, archive_folder_prefix):
    """Create and return daily archive folder path."""
    date_string = datetime.datetime.now().strftime(archive_folder_format)
    archive_folder_name = f"{date_string}_{archive_folder_prefix}"
    archive_folder_path = os.path.join(archive_folder_base, archive_folder_name)
    os.makedirs(archive_folder_path, exist_ok=True)
    return archive_folder_path

def log_move_event(writer, file_owner, file_name, action, source_folder_name, source_path, destination_path, page_count, file_size, last_accessed):
    """Write file move event to CSV log."""
    timestamp = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    writer.writerow([
        timestamp,
        file_owner,
        file_name,
        action,
        page_count,
        source_folder_name,
        source_path,
        destination_path,
        file_size,
        last_accessed
    ])

def get_file_owner(file_path):
    """Get Windows file owner name."""
    try:
        sd = win32security.GetFileSecurity(file_path, win32security.OWNER_SECURITY_INFORMATION)
        owner_sid = sd.GetSecurityDescriptorOwner()
        name, domain, type = win32security.LookupAccountSid(None, owner_sid)
        if domain:
            return f"{domain}\\{name}"
        else:
            return name
    except win32security.error as e:
        logger.error(f"Failed to get owner for file '{file_path}'. win32security.error: {str(e)}")
        return "Unknown"
    except Exception as e:
        logger.error(f"Unexpected error getting owner for file '{file_path}'. Exception: {str(e)}")
        return "Unknown"

def copy_file_with_security(src, dst):
    """Copy file preserving security attributes."""
    try:
        shutil.copy2(src, dst)
        sd = win32security.GetFileSecurity(src,
                                           win32security.OWNER_SECURITY_INFORMATION |
                                           win32security.DACL_SECURITY_INFORMATION |
                                           win32security.GROUP_SECURITY_INFORMATION)
        win32security.SetFileSecurity(dst,
                                      win32security.OWNER_SECURITY_INFORMATION |
                                      win32security.DACL_SECURITY_INFORMATION |
                                      win32security.GROUP_SECURITY_INFORMATION,
                                      sd)
    except PermissionError as pe:
        logger.warning(f"Permission denied while copying security from '{src}' to '{dst}'. Exception: {str(pe)}")
    except Exception as e:
        logger.error(f"Failed to copy file with security from '{src}' to '{dst}'. Exception: {str(e)}")
        raise

def verify_file_owner(original_path, copied_path):
    """Verify copied file has same owner as original."""
    original_owner = get_file_owner(original_path)
    copied_owner = get_file_owner(copied_path)
    if original_owner != copied_owner:
        logger.error(f"Ownership mismatch: Original '{original_owner}' vs Copied '{copied_owner}' for file '{original_path}'")
    else:
        print(f"Ownership verified for '{original_path}'")

def is_network_path(path):
    """Check if path is a network path."""
    return path.startswith('\\\\') or path.startswith('//')

def get_unique_file_name(path, name):
    """Generate unique filename by adding counter if needed."""
    base_name, extension = os.path.splitext(name)
    unique_name = name
    counter = 1
    while os.path.exists(os.path.join(path, unique_name)):
        unique_name = f"{base_name} ({counter}){extension}"
        counter += 1
    return unique_name

def is_date_valid_including_weekend(file_name):
    """Check if filename date is within 4 days before/after today."""
    print(f"Validating date in filename: {file_name}")
    # Match exactly 8 digits at start, no digit following
    match = re.match(r'^(\d{8})(?!\d)', file_name)
    if match:
        print(f"Match found: {match.group(1)}")
        file_date_str = match.group(1)
        try:
            file_date = datetime.datetime.strptime(file_date_str, "%Y%m%d").date()
            today = datetime.datetime.now().date()
            valid_date_range_start = today - datetime.timedelta(days=4)
            valid_date_range_end = today + datetime.timedelta(days=4)
            is_valid = valid_date_range_start <= file_date <= valid_date_range_end
            print(f"File date: {file_date} | Valid Range: {valid_date_range_start} to {valid_date_range_end} | Is Valid: {is_valid}")
            return is_valid
        except ValueError:
            print(f"ValueError: Invalid date format in filename: {file_date_str}")
            return False
    else:
        print("No match found for date.")
        return False

def get_pdf_page_count(file_path):
    """Get PDF page count or empty string if error."""
    try:
        with fitz.open(file_path) as pdf_document:
            return pdf_document.page_count
    except Exception:
        return ""

def get_pdf_file_size(file_path):
    """Get file size in bytes or empty string if error."""
    try:
        size_bytes = os.path.getsize(file_path)
        return size_bytes
    except Exception:
        return ""

def is_pdf_password_protected(file_path):
    """Check if PDF requires password."""
    try:
        with fitz.open(file_path) as pdf_document:
            return pdf_document.needs_pass
    except Exception:
        return False

def create_template_ini(config_directory):
    """Create template INI file for user configuration."""
    template_content = """[GeneralSettings]
file_extension = .pdf
log_file_name_base = LOG
log_date_format = %Y%m%d
log_headers = DATE-TIME, FILE-OWNER, FILE-NAME, ACTION, PDF-PAGE-COUNT, SOURCE-FOLDER, SOURCE-PATH, DESTINATION-PATH, PDF-FILE-SIZE, LAST-ACCESSED
archive_folder_prefix = Archive
archive_folder_format = %Y%m%d

[Paths]
source_folder = source path
destination_folder = destination path
archive_folder_base = archive path
log_folder_path = log path
"""
    template_file_path = os.path.join(config_directory, 'template.ini')
    try:
        with open(template_file_path, 'w') as template_file:
            template_file.write(template_content)
        print(f"No INI files found. A template INI file has been created at '{template_file_path}'. Please configure it before running the script again.")
        sys.exit(0)
    except Exception as e:
        print(f"Failed to create template INI file. Exception: {str(e)}")
        sys.exit(1)

def is_access_denied_error(exception):
    """Check if exception is access denied error."""
    return (isinstance(exception, PermissionError) or 
            (hasattr(exception, 'winerror') and exception.winerror == 5) or
            "Access is denied" in str(exception))

def is_file_in_use_error(exception):
    """Check if exception is 'file in use' error."""
    return (hasattr(exception, 'winerror') and exception.winerror == 32 or
            "being used by another process" in str(exception))

def check_disk_space(file_path, destination_folder, archive_folder):
    """Check if sufficient disk space exists for file operations."""
    try:
        file_size = os.path.getsize(file_path)
        
        # Check destination space
        dest_stat = shutil.disk_usage(destination_folder)
        dest_free = dest_stat.free
        
        # Check archive space
        archive_stat = shutil.disk_usage(archive_folder)
        archive_free = archive_stat.free
        
        # 10% safety margin for each operation
        required_dest_space = file_size * 1.1
        required_archive_space = file_size * 1.1
        
        if dest_free < required_dest_space:
            logger.error(f"Insufficient disk space in destination: {dest_free:,} bytes free, {required_dest_space:,} bytes required")
            return False
            
        if archive_free < required_archive_space:
            logger.error(f"Insufficient disk space in archive: {archive_free:,} bytes free, {required_archive_space:,} bytes required")
            return False
            
        return True
        
    except Exception as e:
        logger.error(f"Failed to check disk space: {str(e)}")
        return False

def set_console_title(title):
    """Set console window title for easy identification."""
    if WINDOWS_CONSOLE_AVAILABLE:
        try:
            os.system(f'title {title}')
        except Exception:
            pass

def minimize_console_window():
    """Minimize the console window after startup."""
    if WINDOWS_CONSOLE_AVAILABLE:
        try:
            # Try multiple methods to get console window
            console_window = None
            
            # Method 1: Use kernel32 GetConsoleWindow (most reliable)
            try:
                import ctypes
                kernel32 = ctypes.windll.kernel32
                console_window = kernel32.GetConsoleWindow()
            except Exception as e:
                print(f"[DEBUG] kernel32.GetConsoleWindow failed: {e}")
            
            # Method 2: Find window by title as fallback
            if not console_window or console_window == 0:
                def enum_windows_callback(hwnd, windows):
                    if win32gui.IsWindowVisible(hwnd):
                        window_title = win32gui.GetWindowText(hwnd)
                        if "File Mover" in window_title or "file_mover_logger.py" in window_title:
                            windows.append(hwnd)
                    return True
                
                windows = []
                win32gui.EnumWindows(enum_windows_callback, windows)
                if windows:
                    console_window = windows[0]
            
            # Method 3: Try alternative ctypes approach as last resort
            if not console_window or console_window == 0:
                try:
                    import ctypes
                    from ctypes import wintypes
                    kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
                    kernel32.GetConsoleWindow.restype = wintypes.HWND
                    console_window = kernel32.GetConsoleWindow()
                except Exception as e:
                    print(f"[DEBUG] Alternative ctypes approach failed: {e}")
            
            if console_window and console_window != 0:
                # Minimize the window
                win32gui.ShowWindow(console_window, win32con.SW_MINIMIZE)
                print("[MINIMIZED] Console minimized - check taskbar to view progress")
                return True
            else:
                print("[INFO] Could not find console window - running in normal mode")
                return False
                
        except Exception as e:
            print(f"[INFO] Could not minimize console: {e}")
    return False

def restore_console_window():
    """Restore console window when script completes."""
    if WINDOWS_CONSOLE_AVAILABLE:
        try:
            # Use kernel32 to get console window handle
            import ctypes
            kernel32 = ctypes.windll.kernel32
            console_window = kernel32.GetConsoleWindow()
            if console_window != 0:
                win32gui.ShowWindow(console_window, win32con.SW_RESTORE)
                win32gui.SetForegroundWindow(console_window)
        except Exception as e:
            print(f"[DEBUG] Could not restore console window: {e}")

def format_time_remaining(seconds):
    """Format seconds into human-readable time."""
    if seconds < 60:
        return f"{int(seconds)}s"
    elif seconds < 3600:
        minutes = int(seconds // 60)
        secs = int(seconds % 60)
        return f"{minutes}m {secs}s"
    else:
        hours = int(seconds // 3600)
        minutes = int((seconds % 3600) // 60)
        return f"{hours}h {minutes}m"

def print_progress_header(total_files, config_name):
    """Print formatted progress header."""
    print(f"\n{'='*80}")
    print(f"[PROCESSING] {config_name}")
    print(f"[TOTAL FILES] {total_files}")
    if WINDOWS_CONSOLE_AVAILABLE:
        print(f"[TIP] Click taskbar 'File Mover' icon to view progress anytime")
    print(f"{'='*80}")

def print_file_progress(current, total, file_name, start_time):
    """Print current file progress with ETA."""
    elapsed = time.time() - start_time
    if current > 0:
        avg_time_per_file = elapsed / current
        remaining_files = total - current
        eta_seconds = remaining_files * avg_time_per_file
        eta_str = format_time_remaining(eta_seconds)
        speed = current / (elapsed / 60) if elapsed > 0 else 0
        print(f"\n[PROGRESS] [{current:3d}/{total:3d}] {file_name}")
        print(f"[SPEED] {speed:.1f} files/min | [ETA] {eta_str}")
    else:
        print(f"\n[PROGRESS] [{current:3d}/{total:3d}] {file_name}")

def print_operation_status(operation, path=""):
    """Print detailed operation status."""
    status_icons = {
        "validating": "[VALIDATE]",
        "copying_dest": "[COPY->DEST]",
        "copying_archive": "[COPY->ARCHIVE]", 
        "removing_source": "[DELETE]",
        "success": "[SUCCESS]",
        "error": "[ERROR]",
        "skipped": "[SKIP]"
    }
    icon = status_icons.get(operation, "[INFO]")
    if path:
        print(f"   {icon} {operation.replace('_', ' ').title()}: {os.path.basename(path)}")
    else:
        print(f"   {icon} {operation.replace('_', ' ').title()}")

def print_summary_stats(stats):
    """Print final processing statistics."""
    print(f"\n{'='*80}")
    print(f"[SUMMARY] PROCESSING SUMMARY")
    print(f"{'='*80}")
    print(f"[SUCCESS] Successfully moved: {stats['moved']:3d} files")
    print(f"[SKIPPED] Skipped files:      {stats['skipped']:3d} files")
    print(f"[FAILED]  Failed files:       {stats['failed']:3d} files")
    print(f"[TOTAL]   Total processed:    {stats['total']:3d} files")
    print(f"[TIME]    Total time:        {format_time_remaining(stats['elapsed'])}")
    if stats['moved'] > 0:
        print(f"[SPEED]   Average speed:      {stats['moved']/(stats['elapsed']/60):.1f} files/min")
    print(f"{'='*80}\n")

# Instance locking functions

def create_lock_file():
    """Create lock file to prevent multiple instances."""
    lock_file_path = os.path.join(script_dir, 'file_mover.lock')
    
    try:
        # Check if lock file exists and process is still running
        if os.path.exists(lock_file_path):
            try:
                with open(lock_file_path, 'r') as f:
                    existing_pid = int(f.read().strip())
                
                # Check if process with this PID still exists
                try:
                    os.kill(existing_pid, 0)  # Signal 0 checks process existence
                    logger.error(f"Another instance is already running (PID: {existing_pid})")
                    print(f"Another instance of the script is already running (PID: {existing_pid})")
                    return False
                except (OSError, ProcessLookupError):
                    # Process doesn't exist, remove stale lock file
                    logger.warning(f"Removing stale lock file for non-existent process (PID: {existing_pid})")
                    os.remove(lock_file_path)
            except (ValueError, IOError):
                # Invalid lock file, remove it
                logger.warning("Removing invalid lock file")
                os.remove(lock_file_path)
        
        # Create new lock file with current PID
        current_pid = os.getpid()
        with open(lock_file_path, 'w') as f:
            f.write(str(current_pid))
        
        logger.info(f"Lock file created successfully (PID: {current_pid})")
        return True
        
    except Exception as e:
        logger.error(f"Failed to create lock file: {str(e)}")
        return False

def cleanup_lock_file():
    """Remove lock file on exit."""
    lock_file_path = os.path.join(script_dir, 'file_mover.lock')
    try:
        if os.path.exists(lock_file_path):
            with open(lock_file_path, 'r') as f:
                pid_in_file = int(f.read().strip())
            
            # Only remove if it's our PID
            if pid_in_file == os.getpid():
                os.remove(lock_file_path)
                logger.info("Lock file removed successfully")
            else:
                logger.warning(f"Lock file PID mismatch: {pid_in_file} vs {os.getpid()}")
    except Exception as e:
        logger.error(f"Failed to remove lock file: {str(e)}")

def signal_handler(signum, frame):
    """Handle termination signals for clean shutdown."""
    logger.info(f"Received signal {signum}, cleaning up...")
    cleanup_lock_file()
    sys.exit(0)

# Main processing function

def process_config(config_file, config):
    """Process files according to configuration settings."""
    # Extract General Settings
    try:
        file_extension = config.get('GeneralSettings', 'file_extension')
        log_file_name_base = config.get('GeneralSettings', 'log_file_name_base')
        log_date_format = config.get('GeneralSettings', 'log_date_format')
        log_headers = [header.strip() for header in config.get('GeneralSettings', 'log_headers').split(',')]
        archive_folder_prefix = config.get('GeneralSettings', 'archive_folder_prefix')
        archive_folder_format = config.get('GeneralSettings', 'archive_folder_format')
    except (configparser.NoSectionError, configparser.NoOptionError) as e:
        logger.error(f"Configuration error in '{config_file}': {str(e)}")
        return

    # Extract Paths
    try:
        source_folder = config.get('Paths', 'source_folder')
        destination_folder = config.get('Paths', 'destination_folder')
        archive_folder_base = config.get('Paths', 'archive_folder_base')
        log_folder_path = config.get('Paths', 'log_folder_path')
    except (configparser.NoSectionError, configparser.NoOptionError) as e:
        logger.error(f"Paths configuration error in '{config_file}': {str(e)}")
        return

    # Validate and create directories
    if not os.path.exists(source_folder):
        logger.error(f"Source folder does not exist: {source_folder}")
        return
    if not os.path.exists(destination_folder):
        try:
            os.makedirs(destination_folder, exist_ok=True)
        except Exception as e:
            logger.error(f"Failed to create destination folder: {destination_folder}. Exception: {str(e)}")
            return
    if not os.path.exists(archive_folder_base):
        try:
            os.makedirs(archive_folder_base, exist_ok=True)
        except Exception as e:
            logger.error(f"Failed to create archive base folder: {archive_folder_base}. Exception: {str(e)}")
            return
    if not os.path.exists(log_folder_path):
        try:
            os.makedirs(log_folder_path, exist_ok=True)
        except Exception as e:
            print(f"Failed to create log folder: {log_folder_path}. Exception: {str(e)}")
            return

    # Create daily archive folder
    daily_archive_folder = get_daily_archive_folder(archive_folder_base, archive_folder_format, archive_folder_prefix)

    # Get list of PDF files
    try:
        files = [f for f in os.listdir(source_folder) if f.lower().endswith(file_extension.lower())]
    except Exception as e:
        logger.error(f"Error accessing source folder: {source_folder}. Exception: {str(e)}")
        return

    # Process files with CSV logging
    log_file_path = get_log_file_path(log_folder_path, log_file_name_base, log_date_format)
    
    # Initialize progress tracking
    stats = {
        'moved': 0,
        'skipped': 0, 
        'failed': 0,
        'total': len(files),
        'start_time': time.time()
    }
    
    # Show processing header
    print_progress_header(len(files), os.path.basename(config_file))
    
    try:
        file_exists = os.path.isfile(log_file_path)
        # Use context manager to ensure file is always closed
        with open(log_file_path, 'a', newline='', encoding='utf-8') as log_file:
            writer = csv.writer(log_file)
            if not file_exists:
                writer.writerow(log_headers)
            
            # Process each file within the context manager
            for index, file_name in enumerate(files, 1):
                full_file_path = os.path.join(source_folder, file_name)

                # Update console title with current file progress
                set_console_title(f"File Mover - [{index}/{len(files)}] {file_name[:30]}...")

                # Show progress for current file
                print_file_progress(index, len(files), file_name, stats['start_time'])

                # Skip files with prefixes
                if (file_name.startswith('__CHECK-DATE__') or
                    file_name.startswith('__REMOVE-PASSWORD__') or
                    file_name.startswith('__PROBLEMATIC__') or
                    file_name.startswith('__ACCESS-DENIED__')):
                    print_operation_status("skipped", "Already processed")
                    stats['skipped'] += 1
                    continue

                # Validate date in filename
                print_operation_status("validating", "Date format")
                is_valid_date = is_date_valid_including_weekend(file_name)

                # Get file details
                original_file_owner = get_file_owner(full_file_path)
                page_count = get_pdf_page_count(full_file_path)
                file_size = get_pdf_file_size(full_file_path)
                source_folder_name = os.path.basename(source_folder)
                source_path = full_file_path

                # Get last accessed time
                try:
                    last_access_time = os.path.getatime(full_file_path)
                    last_accessed = datetime.datetime.fromtimestamp(last_access_time).strftime("%Y-%m-%d %H:%M:%S")
                except Exception as e:
                    logger.error(f"Failed to get last accessed time for file '{full_file_path}'. Exception: {str(e)}")
                    last_accessed = "Unknown"

                # Handle 0kb files
                if file_size != "" and file_size == 0:
                    print_operation_status("skipped", "0KB file")
                    new_name = '__PROBLEMATIC__' + file_name
                    new_name = get_unique_file_name(source_folder, new_name)
                    new_full_path = os.path.join(source_folder, new_name)
                    try:
                        os.rename(full_file_path, new_full_path)
                        stats['skipped'] += 1
                        continue
                    except Exception as e:
                        logger.error(f"Failed to rename 0kb file: {full_file_path} to {new_full_path}. Exception: {str(e)}")
                        stats['failed'] += 1
                    continue

                # Handle password-protected PDFs
                if is_pdf_password_protected(full_file_path):
                    print_operation_status("skipped", "Password protected")
                    new_name = '__REMOVE-PASSWORD__' + file_name
                    new_name = get_unique_file_name(source_folder, new_name)
                    new_full_path = os.path.join(source_folder, new_name)
                    try:
                        os.rename(full_file_path, new_full_path)
                        stats['skipped'] += 1
                    except Exception as e:
                        logger.error(f"Failed to rename password-protected file: {full_file_path} to {new_full_path}. Exception: {str(e)}")
                        stats['failed'] += 1
                    continue

                # Handle invalid date files
                if not is_valid_date:
                    print_operation_status("skipped", "Invalid date")
                    if not file_name.startswith("__CHECK-DATE__"):
                        new_name = "__CHECK-DATE__" + file_name
                        new_name = get_unique_file_name(source_folder, new_name)
                        new_full_path = os.path.join(source_folder, new_name)
                        try:
                            os.rename(full_file_path, new_full_path)
                            stats['skipped'] += 1
                        except Exception as e:
                            logger.error(f"Failed to rename file: {full_file_path} to {new_full_path}. Exception: {str(e)}")
                            stats['failed'] += 1
                    continue

                # Check disk space before operations
                if not check_disk_space(full_file_path, destination_folder, daily_archive_folder):
                    print_operation_status("error", "Insufficient disk space")
                    logger.error(f"Insufficient disk space for file '{full_file_path}'. Skipping.")
                    stats['failed'] += 1
                    continue

                # ATOMIC OPERATION: Copy first, then delete source only if both succeed
                unique_destination_name = get_unique_file_name(destination_folder, file_name)
                unique_destination_path = os.path.join(destination_folder, unique_destination_name)
                unique_archive_name = get_unique_file_name(daily_archive_folder, file_name)
                unique_archive_path = os.path.join(daily_archive_folder, unique_archive_name)
                
                destination_success = False
                archive_success = False
                
                try:
                    # Step 1: Copy to destination (source still safe)
                    print_operation_status("copying_dest")
                    if not is_network_path(destination_folder):
                        copy_file_with_security(full_file_path, unique_destination_path)
                    else:
                        shutil.copy2(full_file_path, unique_destination_path)
                    destination_success = True
                    
                    # Step 2: Copy to archive (source still safe)
                    print_operation_status("copying_archive")
                    if not is_network_path(daily_archive_folder):
                        copy_file_with_security(full_file_path, unique_archive_path)
                        verify_file_owner(full_file_path, unique_archive_path)
                    else:
                        shutil.copy2(full_file_path, unique_archive_path)
                    archive_success = True
                    
                    # Step 3: Delete source only if both copies succeeded
                    if destination_success and archive_success:
                        print_operation_status("removing_source")
                        logger.info(f"Both copies successful, removing source file: {full_file_path}")
                        os.remove(full_file_path)
                        
                        # Log successful move
                        log_move_event(
                            writer=writer,
                            file_owner=original_file_owner,
                            file_name=file_name,
                            action="Move",
                            source_folder_name=source_folder_name,
                            source_path=os.path.dirname(source_path),
                            destination_path=os.path.dirname(unique_destination_path),
                            page_count=page_count,
                            file_size=file_size,
                            last_accessed=last_accessed
                        )
                        log_file.flush()  # Force write to disk
                        print_operation_status("success")
                        stats['moved'] += 1
                    else:
                        print_operation_status("error", "Copy operations failed")
                        logger.error(f"Copy operations failed - source file preserved: {full_file_path}")
                        stats['failed'] += 1
                        
                except Exception as e:
                    print_operation_status("error", str(e)[:50])
                    logger.error(f"Failed to process file: {full_file_path}. Exception: {str(e)}")
                    stats['failed'] += 1
                    
                    # Clean up partial copies
                    if destination_success:
                        try:
                            if os.path.exists(unique_destination_path):
                                os.remove(unique_destination_path)
                                logger.info(f"Cleaned up partial destination copy: {unique_destination_path}")
                        except Exception as cleanup_error:
                            logger.error(f"Failed to clean up destination copy: {cleanup_error}")
                    
                    if archive_success:
                        try:
                            if os.path.exists(unique_archive_path):
                                os.remove(unique_archive_path)
                                logger.info(f"Cleaned up partial archive copy: {unique_archive_path}")
                        except Exception as cleanup_error:
                            logger.error(f"Failed to clean up archive copy: {cleanup_error}")
                    
                    # Handle specific error types
                    if is_access_denied_error(e):
                        new_name = "__ACCESS-DENIED__" + file_name
                        new_name = get_unique_file_name(source_folder, new_name)
                        new_full_path = os.path.join(source_folder, new_name)
                        try:
                            os.rename(full_file_path, new_full_path)
                            logger.warning(f"Renamed file with access denied error: {full_file_path} to {new_full_path}")
                        except Exception as rename_error:
                            logger.error(f"Failed to rename file with access denied: {full_file_path} to {new_full_path}. Exception: {str(rename_error)}")
                    elif is_file_in_use_error(e):
                        logger.warning(f"File is in use, skipping: {full_file_path}")
                    else:
                        logger.error(f"Unexpected error processing file: {full_file_path}. Exception: {str(e)}")

    except Exception as e:
        logger.error(f"Failed to open log file: {log_file_path}. Exception: {str(e)}")
        return
    
    # Calculate final statistics and show summary
    stats['elapsed'] = time.time() - stats['start_time']
    print_summary_stats(stats)

# Global exception handler

def main():
    """Main entry point with instance locking and configuration processing."""
    try:
        # Set console title for easy identification
        set_console_title("File Mover - Starting...")
        
        # Check for existing instance and create lock
        if not create_lock_file():
            sys.exit(1)
        
        # Register cleanup handlers
        atexit.register(cleanup_lock_file)
        signal.signal(signal.SIGINT, signal_handler)  # Ctrl+C
        signal.signal(signal.SIGTERM, signal_handler)  # Termination
        
        logger.info("Running file mover with atomic operations and instance locking.")
        
        # Show startup message and minimize after brief delay
        print("[STARTING] File Mover Starting...")
        print("[TIP] Window will minimize in 3 seconds for multitasking")
        print("[INFO] Check taskbar for 'File Mover' to view progress anytime")
        print("[COUNTDOWN] Minimizing in: 3...")
        time.sleep(1)
        print("[COUNTDOWN] Minimizing in: 2...")
        time.sleep(1)
        print("[COUNTDOWN] Minimizing in: 1...")
        time.sleep(1)
        
        # Minimize console window
        minimized = minimize_console_window()
        if not minimized:
            print("[INFO] Running in normal window mode")

        # Setup configuration directory
        config_directory = os.path.join(script_dir, 'configs')

        if not os.path.exists(config_directory):
            os.makedirs(config_directory, exist_ok=True)

        # Get INI files
        ini_files = [file for file in os.listdir(config_directory) if file.lower().endswith('.ini')]

        if not ini_files:
            create_template_ini(config_directory)

        # Process each configuration
        total_configs = len(ini_files)
        for config_index, file in enumerate(ini_files, 1):
            config_file_path = os.path.join(config_directory, file)
            
            # Update console title with progress
            set_console_title(f"File Mover - Config {config_index}/{total_configs}: {file}")
            
            logger.info(f"Processing configuration: {config_file_path}")
            config = load_config(config_file_path)
            process_config(config_file_path, config)
        
        # Clean up on successful completion
        cleanup_lock_file()
        logger.info("Script completed successfully")
        
        # Restore and show completion
        set_console_title("File Mover - Completed!")
        restore_console_window()
        print("\n[COMPLETE] ALL PROCESSING COMPLETE!")
        print("[INFO] Check logs and CSV files for detailed results")
        print("[AUTO-CLOSE] Auto-closing in 10 seconds...")
        
        # Auto-close for unattended operation
        time.sleep(10)
        
    except Exception as e:
        # Restore window on error so user can see the problem
        restore_console_window()
        set_console_title("File Mover - ERROR!")
        try:
            logger.error(f"An unhandled exception occurred. Exception: {str(e)}")
            traceback_str = ''.join(traceback.format_exception(None, e, e.__traceback__))
            logger.error(traceback_str)
        except Exception:
            print(f"An unhandled exception occurred and could not be logged. Exception: {str(e)}")
        
        print("\n[ERROR] SCRIPT FAILED - Check logs for details")
        print("[AUTO-CLOSE] Auto-closing in 15 seconds...")
        time.sleep(15)  # Longer delay for error visibility

# Run the script
if __name__ == "__main__":
    main()
