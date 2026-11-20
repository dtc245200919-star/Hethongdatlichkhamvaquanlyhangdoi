const doctorSelect = document.querySelector("#doctor");
const dateInput = document.querySelector("#date");
const slotSelect = document.querySelector("#slot");
const message = document.querySelector("#message");

const today = new Date();
const localDate = new Date(today.getTime() - today.getTimezoneOffset() * 60000)
  .toISOString()
  .slice(0, 10);
dateInput.min = localDate;

async function loadDoctors() {
  const response = await fetch("/doctors");
  if (!response.ok) throw new Error("Không thể tải danh sách bác sĩ.");
  const doctors = await response.json();
  doctorSelect.innerHTML = doctors.map(doctor =>
    `<option value="${doctor.id}">${doctor.name} - ${doctor.specialty}</option>`
  ).join("");
}

async function loadSlots() {
  slotSelect.disabled = true;
  slotSelect.innerHTML = "<option>Đang tải khung giờ...</option>";
  const doctorId = doctorSelect.value;
  if (!doctorId || !dateInput.value) return;

  const response = await fetch(`/doctors/${doctorId}/slots?date=${dateInput.value}`);
  if (!response.ok) {
    const body = await response.json();
    throw new Error(body.error || "Không thể tải khung giờ.");
  }
  const slots = await response.json();
  slotSelect.innerHTML = slots.length
    ? slots.map(slot => `<option value="${slot}">${new Date(slot).toLocaleString("vi-VN")}</option>`).join("")
    : "<option value=\"\">Không còn khung giờ trống</option>";
  slotSelect.disabled = slots.length === 0;
}

doctorSelect.addEventListener("change", () => loadSlots().catch(showError));
dateInput.addEventListener("change", () => loadSlots().catch(showError));

function showError(error) {
  message.textContent = error.message;
  message.style.color = "#b42318";
}

document.querySelector("#booking-form").addEventListener("submit", async event => {
  event.preventDefault();
  message.textContent = "";

  const payload = {
    patientName: document.querySelector("#patient-name").value.trim(),
    patientPhone: document.querySelector("#patient-phone").value.trim(),
    doctorId: doctorSelect.value,
    scheduledAt: slotSelect.value
  };
  const response = await fetch("/appointments", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload)
  });
  const body = await response.json();
  if (!response.ok) {
    showError(new Error(body.error || "Đặt lịch thất bại."));
    return;
  }
  message.style.color = "#19763b";
  message.textContent = `Đặt lịch thành công. Mã lịch: ${body.id}`;
  await loadSlots();
});

document.querySelector("#lookup-form").addEventListener("submit", async event => {
  event.preventDefault();
  const id = document.querySelector("#appointment-id").value.trim();
  const output = document.querySelector("#lookup-result");
  output.textContent = "";
  const response = await fetch(`/appointments/${encodeURIComponent(id)}`);
  const body = await response.text();
  output.textContent = body ? JSON.stringify(JSON.parse(body), null, 2) : "";
  if (!response.ok) output.textContent = body;
});

loadDoctors()
  .then(() => loadSlots())
  .catch(showError);
