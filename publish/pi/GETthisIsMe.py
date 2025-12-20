import requests
import json
import pandas as pd
import base64
import datetime
import os
import pickle

formId = 1021596
PICKLE_PATH = "last_submitted_id.pkl"

# Load the last processed submittedForm ID if it exists
if os.path.exists(PICKLE_PATH):
    with open(PICKLE_PATH, "rb") as f:
        last_submitted_id = pickle.load(f)
    print(f"Loaded last_submitted_id from pickle: {last_submitted_id}")
else:
    last_submitted_id = 0
    print("No existing pickle found, starting from 0")

# === Settings ===
startTime = datetime.datetime.now()

# === Mobizio Auth ===
sample_string = 'BristolCH.API:dhd70Jja]"2%nsIk'
base64_string = base64.b64encode(sample_string.encode('ascii')).decode('ascii')
print(f'Authorization: Basic {base64_string}')

response = requests.post(
    'https://cloud7.mobizio.com/rest/oauth/token?grant_type=password&client_id=web-console'
    '&username=BristolCH.API&password=dhd70Jja%5D%272%25nsIk'
)
rdata = response.json()
access_token = rdata.get('access_token')
headers = {
    'Authorization': f'Bearer {access_token}',
    'Content-Type': 'application/json'
}
print("Access token acquired.")

params = {
            "start": 0,
            "limit": "5000",
            "column": "id",
            "direction": "1",
            "mql": "archived=false"

        }
cases = requests.get('https://cloud7.mobizio.com/rest/v3/cases', params=params, headers=headers)

data = cases.json()
Casesresults = data.get('results', [])
cases_df = pd.DataFrame(Casesresults)
cases_df = pd.json_normalize(cases_df.to_dict(orient='records'))


params = {
            "start": 0,
            "limit": "5000",
            "column": "id",
            "direction": "1"
}
formName = requests.get(f'https://cloud7.mobizio.com/rest/v3/forms/{formId}', params=params, headers=headers)
formName = formName.json()
formName = formName.get('name')
formName = formName.replace(" ", "_")
print(formName)
excel_path = f"/home/dazzletag/media-button/{formName}.xlsx"

formVersions = requests.get(f'https://cloud7.mobizio.com/rest/v3/forms/{formId}/versions', params=params, headers=headers)

FORM_VERSION_IDS = formVersions.json()
FORM_VERSION_IDS = FORM_VERSION_IDS.get('results', [])
FORM_VERSION_IDS = pd.DataFrame(FORM_VERSION_IDS)
FORM_VERSION_IDS = FORM_VERSION_IDS[['id', 'version', 'createdDateTime']]
FORM_VERSION_IDS = FORM_VERSION_IDS['id'].tolist()
print(FORM_VERSION_IDS)


def fetch_submitted_forms(form_version_id):
    print(f"\nFetching submitted forms for formVersionId={form_version_id}")
    all_pages = []
    x = 0
    while True:
        params = {
            "start": str(x),
            "limit": "1000",
            "column": "id",
            "direction": "1",
            "mql": f"formVersionId={form_version_id}"}


        response = requests.get('https://cloud7.mobizio.com/rest/v3/submittedForms', params=params, headers=headers)
        response.raise_for_status()
        data = response.json()
        results = data.get('results', [])
        if not results:
            break
        df = pd.json_normalize(results)
        all_pages.append(df)
        print(f"Retrieved {len(df)} records at offset {x}")
        x += 1000

    return pd.concat(all_pages, ignore_index=True) if all_pages else pd.DataFrame()

# === Combine data from both form versions ===
all_forms_df = pd.DataFrame()
for form_id in FORM_VERSION_IDS:
    df = fetch_submitted_forms(form_id)
    all_forms_df = pd.concat([all_forms_df, df], ignore_index=True)


if not all_forms_df.empty:
    all_forms_df["id"] = all_forms_df["id"].astype(int)
#last_submitted_id = 0 #29476300
# === Filter to new records only ===
new_records_df = all_forms_df[all_forms_df['id'] > last_submitted_id]
print(f"\nTotal new records found: {len(new_records_df)}")
if new_records_df.empty:
    print("No new records to process.")
else:
    new_records_df.reset_index(drop=True, inplace=True)

# === Download form element details for each new submission ===
#print(new_records_df.to_string())
detail_dfs = []
for index, row in new_records_df.iterrows():
    form_id = str(int(row.get('id_y') or row.get('id')))
    print(f"Fetching elements for submittedFormId: {form_id}")
    try:
        detail = requests.get(f'https://cloud7.mobizio.com/rest/v3/submittedForms/{form_id}/elements', headers=headers)
        detail.raise_for_status()
        detailJson = detail.json()
        df_detail = pd.json_normalize(detailJson)
        df_detail['submittedFormId'] = form_id
        detail_dfs.append(df_detail)
    except requests.exceptions.RequestException as e:
        print(f"Request error for ID {form_id}: {e}")
    except json.JSONDecodeError as e:
        print(f"JSON error for ID {form_id}: {e}")

# === Combine and save ===
if detail_dfs:
    detailDf = pd.concat(detail_dfs, ignore_index=True)
    detable = detailDf
    if 'encodedValue' in detable.columns:
        detable.drop('encodedValue', axis=1, inplace=True)
else:
    print("No new detail data found.")
    #detable = current


detable = detable.merge(cases_df[['id', "customer.fullName","customer.dob","customer.gender",'tenantCaseId','branch.tenantBranchId']], how='left', left_on='caseDataGroupCaseId', right_on='id')

detable = detable[["id_x",
"tenantCaseId",
"customer.fullName","customer.dob","customer.gender",
"branch.tenantBranchId",
"createdBy.id",
"tenantId",
"createdDateTime",
"parentElementId",
"formVersionId",
"formId",
"componentId",
"componentIsDeleted",
"componentName",
"componentLabel",
"componentType",
"componentServiceId",
"componentValues",
"componentDefaultValues",
"componentInstructions",
"caseDataGroupCaseId",
"valueText",
"valueSmallText",
"valueDouble",
"valueBigInt",
"valueDateTime",
"valueDate",
"valueTime",
"valueUser",
"valueUserFullName",
"stringValue",
"valueBlobRef",
"valueAsString",

]]

detable.rename(columns={"customer.dob":"dob","customer.gender":"gender","customer.fullName":"fullName",'id_x': 'id', 'branch.tenantBranchId': 'careHome'}, inplace=True)
detable = detable[ detable["fullName"].notna() & (detable["fullName"].str.strip() != "") ]

# Extract the resident-level fields from detable
people = detable[["fullName", "dob", "gender"]].drop_duplicates()

# Build the wide table exactly as before
detable = (
    detable
    .pivot_table(
        index="fullName",
        columns="componentLabel",
        values="valueText",
        aggfunc="first"
    )
    .reset_index()
)

# Merge dob + gender back in
detable = people.merge(detable, on="fullName", how="left")


# === Write to Excel ===
writer = pd.ExcelWriter(excel_path, engine="xlsxwriter")
detable.to_excel(writer, sheet_name=formName, startrow=1, header=False, index=False)

workbook = writer.book
worksheet = writer.sheets[formName]
(max_row, max_col) = detable.shape
column_settings = [{"header": col} for col in detable.columns]

worksheet.add_table(0, 0, max_row, max_col - 1, {"columns": column_settings, "name": formName})
worksheet.set_column(0, max_col - 1, 12)

writer.close()
print(f"\nExcel file saved to: {excel_path}")
# === Update last_submitted_id pickle ===
if not all_forms_df.empty:
    new_max_id = int(all_forms_df["id"].max())
    if new_max_id > last_submitted_id:
        with open(PICKLE_PATH, "wb") as f:
            pickle.dump(new_max_id, f)
        print(f"Updated last_submitted_id in pickle to: {new_max_id}")
    else:
        print("No higher submittedForm ID found; pickle not updated.")
else:
    print("all_forms_df is empty; nothing to update in pickle.")

endTime = datetime.datetime.now()
print(f"Total runtime: {endTime - startTime}")

