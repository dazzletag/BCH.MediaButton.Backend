
import tkinter as tk
from PIL import Image, ImageTk
from tkinter import filedialog, messagebox, scrolledtext, ttk
import base64
import json
import datetime
import requests
import time
import os
import sys

# ---- Mobizio API Setup ----
username = 'BristolCH.API'
password = 'dhd70Jja]"2%nsIk'

auth_string = f'{username}:{password}'
b64_auth = base64.b64encode(auth_string.encode('ascii')).decode('ascii')

token_url = 'https://cloud7.mobizio.com/rest/oauth/token?grant_type=password&client_id=web-console&username=BristolCH.API&password=dhd70Jja%5D%272%25nsIk'
headers = {
    'Authorization': f'Basic {b64_auth}',
    'Content-Type': 'application/json'
}
response = requests.post('https://cloud7.mobizio.com/rest/oauth/token?grant_type=password&client_id=web-console&username=BristolCH.API&password=dhd70Jja%5D%272%25nsIk')#, data=data)
access_token = response.json().get('access_token')
print(access_token)

if not access_token:
    raise Exception("Failed to retrieve access token.")

headers = {
    'Authorization': f'Bearer {access_token}',
    'Content-Type': 'application/json'
}
params = {
    'start': '0',
    'limit': '1000',
    'column': 'id',
    'direction': '1',
    'mql': 'tenantCaseId!=CASE-2120a,archived=FALSE'
}
residents_url = 'https://cloud7.mobizio.com/rest/v3/cases'
response = requests.get(residents_url, headers=headers, params=params)
residents = response.json().get('results', [])
print(json.dumps(residents))

def resource_path(relative_path):
    """ Get absolute path to resource, works for dev and for PyInstaller """
    base_path = getattr(sys, '_MEIPASS', os.path.dirname(os.path.abspath(__file__)))
    return os.path.join(base_path, relative_path)


# ---- Group by Branch ----
residents_by_branch = {}
for r in residents:
    customer = r.get("customer", {})
    branch = customer.get("branchView", {}).get("name", "Unknown")
    full_name = f"{customer.get('firstName', '')} {customer.get('lastName', '')}".strip()
    case_id = r.get("tenantCaseId")
    if branch and full_name and case_id:
        residents_by_branch.setdefault(branch, []).append({"id": case_id, "name": full_name})

# Filter only known houses
selected_branches = ["Glebe House", "Beech House", "Field House", "Quarry House"]
filtered_residents = {b: r for b, r in residents_by_branch.items() if b in selected_branches}


# ---- GUI Setup ----

root = tk.Tk()
root.title("Activities Record Form (Live)")
root.geometry("1200x800")


# Load and resize logo
logo_path = resource_path("Capture.PNG")
img = Image.open(logo_path).resize((100, 40))
logo_image = ImageTk.PhotoImage(img)

# Frame to hold logo and title
logo_frame = tk.Frame(root)
logo_frame.pack(side="top", fill="x", pady=5)

# Logo on the left
logo_label = tk.Label(logo_frame, image=logo_image)
logo_label.image = logo_image
logo_label.pack(side="left", anchor="w", padx=10)

# Title next to the logo
title_label = tk.Label(logo_frame, text="Group Activity Form", font=("Arial", 18, "bold"))
title_label.pack(side="left", anchor="w", padx=10)


# Main container frame
main_frame = tk.Frame(root)
main_frame.pack(fill="both", expand=True, padx=10, pady=10)

# Left form frame using grid layout
form_frame = tk.Frame(main_frame)
form_frame.pack(side="left", fill="both", expand=True)

# Right resident frame
resident_frame = tk.Frame(main_frame)
resident_frame.pack(side="right", fill="y")

photo_base64_list = []
progress_var = tk.DoubleVar()


def select_photos():
    filepaths = filedialog.askopenfilenames(
        title="Select One Photo Only",
        filetypes=[("Image Files", "*.png *.jpg *.jpeg *.gif")]
    )
    if not filepaths:
        return

    # Take only the first selected photo
    filepath = filepaths[0]
    with open(filepath, "rb") as img_file:
        encoded = base64.b64encode(img_file.read()).decode("utf-8")
        # Strip any data URL prefix if accidentally present
        if encoded.startswith("data:image"):
            encoded = encoded.split(",")[-1]
        photo_base64_list.clear()
        photo_base64_list.append(encoded)

    messagebox.showinfo("Photo Added", "1 photo added. Previous photo (if any) was replaced.")




def load_residents(branch_name):
    for widget in scrollable_frame.winfo_children():
        widget.destroy()
    resident_vars.clear()
    for resident in sorted(filtered_residents.get(branch_name, []), key=lambda r: r["name"]):
        var = tk.BooleanVar()
        tk.Checkbutton(scrollable_frame, text=resident["name"], variable=var).pack(anchor='w')
        resident_vars.append((resident["id"], var))


def on_branch_selected(event):
    selected_branch = branch_var.get()
    load_residents(selected_branch)

def submit_form():
    submit_btn.config(state="disabled")
    data = {
        "activity_location": activity_entry.get(),
        "setting": setting_var.get(),
        "entertainer": entertainer_var.get(),
        "activity_date": date_entry.get(),
        "story": story_text.get("1.0", tk.END).strip(),
        "rating": rating_var.get(),
        "photo_consent": consent_var.get(),
        "photos": photo_base64_list,
        "residents": [res_id for res_id, var in resident_vars if var.get()]
    }

    if not data["activity_location"] or not data["residents"]:
        messagebox.showwarning("Missing Data", "Please complete required fields: Activity Location and Resident Selection.")
        return
    total = len(data["residents"])
    success_count = 0

    for idx, resident_id in enumerate(data["residents"]):

        # Step 1: Get caseDataGroupId
        section_url = f"https://cloud7.mobizio.com/rest/v3/cases/{resident_id}/sections/_lite"
        params = {
            "start": 0,
            "limit": 10,
            "column": "id",
            "direction": 1,
            "mql": "serviceDataGroupLabel=Social History/Activities"
        }

        section_res = requests.get(section_url, headers=headers, params=params)
        results = section_res.json().get("results", [])
        if not results:
            print(f"Could not get caseDataGroupId for resident {resident_id}")
            continue

        case_data_group_id = results[0]["id"]
        case_id = results[0]["caseId"]

        # Step 2: Build form elements
        elements = [
            {
                "component": {"id": 10657228, "type": "textbox"},
                "componentId": 10657228,
                "formVersionId": 1185142,
                "formId": 1024129,
                "valueSmallText": data["activity_location"],
                "valueAsString": data["activity_location"],
                "componentLabel": "Activity/Visit location",
                "componentType": "textbox",
                "position": 1
            },
            {
                "component": {"id": 10657229, "type": "radio"},
                "componentId": 10657229,
                "formVersionId": 1185142,
                "formId": 1024129,
                "valueSmallText": data["setting"],
                "valueAsString": data["setting"],
                "componentLabel": "Setting",
                "componentType": "radio",
                "position": 2
            },
            {
                "component": {"id": 10657230, "type": "dropdown"},
                "componentId": 10657230,
                "formVersionId": 1185142,
                "formId": 1024129,
                "valueSmallText": data["entertainer"],
                "valueAsString": data["entertainer"],
                "componentLabel": "Who was the entertainer?",
                "componentType": "dropdown",
                "position": 3
            },
            {
                "component": {"id": 10657231, "type": "date"},
                "componentId": 10657231,
                "formVersionId": 1185142,
                "formId": 1024129,
                "valueDate": data["activity_date"],
                "componentLabel": "Date of Activity",
                "componentType": "date",
                "position": 4
            },
            {
                "component": {"id": 10657232, "type": "textarea"},
                "componentId": 10657232,
                "formVersionId": 1185142,
                "formId": 1024129,
                "valueText": data["story"],
                "componentLabel": "Any stories",
                "componentType": "textarea",
                "position": 5
            },
            {
                "component": {"id": 10657233, "type": "radio"},
                "componentId": 10657233,
                "formVersionId": 1185142,
                "formId": 1024129,
                "valueSmallText": data["rating"],
                "valueAsString": data["rating"],
                "componentLabel": "How would you rate the activity?",
                "componentType": "radio",
                "position": 6
            },
            {
                "component": {"id": 10657234, "type": "radio"},
                "componentId": 10657234,
                "formVersionId": 1185142,
                "formId": 1024129,
                "valueSmallText": data["photo_consent"],
                "valueAsString": data["photo_consent"],
                "componentLabel": "Resident has consented to photos",
                "componentType": "radio",
                "position": 8
            }
        ]

        # Step 3: Add photo using encodedValue
        if data["photos"]:
            elements.append({
                "component": {
                    "id": 10657235,
                    "type": "photo"
                },
                "componentId": 10657235,
                "componentType": "photo",
                "formVersionId": 1185142,
                "formId": 1024129,
                "encodedValue": data["photos"][0],
                "position": 9  # Ensure it's a base64-encoded JPEG string
            })

        # Step 4: Submit to Mobizio
        payload = {
            "caseDataGroupId": case_data_group_id,
            "caseDataGroup": {
                "caseId": case_id,
                "id": case_id,
                "serviceDataGroupId": 1008895,
                "tenantId": 1000472
            },
            "elements": elements,
            "formVersionId": 1185142,
            "tenantId": 1000472,
            "isDeleted": "false",
            "isDraft": "false"
        }

        with open("submitted_payload.json", "w", encoding="utf-8") as f:
            json.dump(payload, f, indent=2)


        form_res = requests.post(
            "https://cloud7.mobizio.com/rest/v3/submittedForms",
            headers=headers,
            data=json.dumps(payload)
        )

        if form_res.status_code == 200 or form_res.status_code == 201:
            success_count += 1
        else:
            print(f"Submission failed for resident {resident_id}: {form_res.text}")

        progress = ((idx + 1) / total) * 100
        progress_var.set(progress)
        root.update_idletasks()

    messagebox.showinfo("Submission Complete", f"Successfully submitted for {success_count} resident(s).")
    progress_var.set(0)
    root.update_idletasks()
    submit_btn.config(state="normal")



# Form widgets (left)
row = 0

tk.Label(form_frame, text="Select Branch:").grid(row=row, column=0, sticky="w", padx=10, pady=5)
branch_var = tk.StringVar()
branch_dropdown = ttk.Combobox(form_frame, textvariable=branch_var, state="readonly", width=60)
branch_dropdown.grid(row=row, column=1, padx=10, pady=5, sticky="w")
branch_dropdown['values'] = list(filtered_residents.keys())
branch_dropdown.bind("<<ComboboxSelected>>", on_branch_selected)
row += 1


# Activity Location
tk.Label(form_frame, text="Activity/Visit Location:").grid(row=row, column=0, sticky="w", padx=10, pady=5)
activity_entry = tk.Entry(form_frame, width=70)
activity_entry.grid(row=row, column=1, padx=10, pady=5, sticky="w")
row += 1

# Setting
tk.Label(form_frame, text="Setting:").grid(row=row, column=0, sticky="w", padx=10, pady=5)
setting_var = tk.StringVar(value="Group")
setting_frame = tk.Frame(form_frame)
setting_frame.grid(row=row, column=1, sticky="w", padx=10)
for option in ["Group", "One to one", "Bedside"]:
    tk.Radiobutton(setting_frame, text=option, variable=setting_var, value=option).pack(side="left")
row += 1

# Entertainer
tk.Label(form_frame, text="Who was the entertainer?").grid(row=row, column=0, sticky="w", padx=10, pady=5)
entertainer_var = tk.StringVar()
entertainer_dropdown = ttk.Combobox(form_frame, textvariable=entertainer_var, width=67)
entertainer_dropdown['values'] = [
    "Bristol Care Homes (internal)", "Day Trip", "Sound Voice", "Maisie Aitchison", "Alive Activities",
    "Chris Auburn Singer", "Tara Baggott", "Thornbury Town Band", "Birds of Prey Discovery", "Jordan Lee Brailsford"
]
entertainer_dropdown.grid(row=row, column=1, padx=10, pady=5, sticky="w")
row += 1

# Date
tk.Label(form_frame, text="Date of Activity (YYYY-MM-DD):").grid(row=row, column=0, sticky="w", padx=10, pady=5)
date_entry = tk.Entry(form_frame, width=70)
date_entry.insert(0, datetime.date.today().isoformat())
date_entry.grid(row=row, column=1, padx=10, pady=5, sticky="w")
row += 1

# Story
tk.Label(form_frame, text="Any Stories?").grid(row=row, column=0, sticky="nw", padx=10, pady=5)
story_text = scrolledtext.ScrolledText(form_frame, width=70, height=5)
story_text.grid(row=row, column=1, padx=10, pady=5, sticky="w")
row += 1

# Rating
tk.Label(form_frame, text="How would you rate the activity?").grid(row=row, column=0, sticky="w", padx=10, pady=5)
rating_var = tk.StringVar(value="5 Stars")
rating_frame = tk.Frame(form_frame)
rating_frame.grid(row=row, column=1, sticky="w", padx=10)
for option in ["5 Stars", "4 Stars", "3 Stars", "2 Stars", "1 Star"]:
    tk.Radiobutton(rating_frame, text=option, variable=rating_var, value=option).pack(side="left")
row += 1

# Photo consent
tk.Label(form_frame, text="Resident has consented to photos?").grid(row=row, column=0, sticky="w", padx=10, pady=5)
consent_var = tk.StringVar(value="Yes")
consent_frame = tk.Frame(form_frame)
consent_frame.grid(row=row, column=1, sticky="w", padx=10)
for option in ["Yes", "No"]:
    tk.Radiobutton(consent_frame, text=option, variable=consent_var, value=option).pack(side="left")
row += 1

# Upload Photo button
upload_btn = tk.Button(form_frame, text="Upload Photo", command=select_photos)

upload_btn.grid(row=row, column=1, sticky="w", padx=10, pady=10)
row += 1

# Submit and progress
submit_btn = tk.Button(form_frame, text="Submit Activity Record", command=submit_form)

submit_btn.grid(row=row, column=1, sticky="w", padx=10, pady=(0, 5))
row += 1

progress_var = tk.DoubleVar()
progress_bar = ttk.Progressbar(form_frame, variable=progress_var, maximum=100)
progress_bar.grid(row=row, column=0, columnspan=2, sticky="we", padx=10, pady=5)


# Scrollable resident list (right)
canvas = tk.Canvas(resident_frame, height=700)
scrollbar = tk.Scrollbar(resident_frame, orient="vertical", command=canvas.yview)
scrollable_frame = tk.Frame(canvas)

def _on_mousewheel(event):
    canvas.yview_scroll(int(-1 * (event.delta / 120)), "units")

scrollable_frame.bind(
    "<Configure>",
    lambda e: canvas.configure(scrollregion=canvas.bbox("all"))
)
canvas.bind_all("<MouseWheel>", _on_mousewheel)


canvas.create_window((0, 0), window=scrollable_frame, anchor="nw")
canvas.configure(yscrollcommand=scrollbar.set)

canvas.pack(side="left", fill="both", expand=True)
scrollbar.pack(side="right", fill="y")

resident_vars = []

root.mainloop()


